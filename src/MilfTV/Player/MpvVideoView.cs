using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using MilfTV.Core;

namespace MilfTV.Player;

/// <summary>Surface vidéo : mpv dessine directement dans le framebuffer OpenGL d'Avalonia,
/// ce qui permet de superposer librement l'interface (contrôles, guide, listes) à la vidéo.</summary>
public sealed class MpvVideoView : OpenGlControlBase
{
    public static readonly StyledProperty<MpvPlayer?> PlayerProperty =
        AvaloniaProperty.Register<MpvVideoView, MpvPlayer?>(nameof(Player));

    public MpvPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    private const int GlColorBufferBit = 0x4000;

    private IntPtr _renderCtx;
    private GlInterface? _gl;
    private MpvPlayer? _boundPlayer;
    private readonly MpvGetProcAddress _getProc;
    private readonly MpvRenderUpdateCallback _onUpdate;
    private IntPtr _apiTypePtr;
    private IntPtr _initParamsPtr;
    private IntPtr _fboPtr;
    private IntPtr _flipPtr;
    private IntPtr _blockPtr;
    private int _pendingFrame;
    private long _updateStamp;
    private long _requestStamp;

    /// <summary>Mesures de cadence (diagnostic) : délai entre « nouvelle image prête » (mpv) et son dessin, durée du dessin.</summary>
    internal static class Timing
    {
        public static long Frames, Over20, Over40, LatSum, LatMax, DrawSum, DrawMax, QueueSum, QueueMax;
        /// <summary>Délai récent (moyenne glissante, ms) entre « image prête » et son affichage.</summary>
        public static double RecentMs;
        public static void Reset() => Frames = Over20 = Over40 = LatSum = LatMax = DrawSum = DrawMax = QueueSum = QueueMax = 0;
        private static double Ms(long t) => t * 1000.0 / Stopwatch.Frequency;
        public static string Report() => Frames == 0 ? "aucune image"
            : $"images={Frames} délai moyen={Ms(LatSum / Frames):0.0} ms max={Ms(LatMax):0.0} ms >20ms={Over20} >40ms={Over40} dessin moyen={Ms(DrawSum / Frames):0.00} ms max={Ms(DrawMax):0.0} ms | file d'attente moyenne={Ms(QueueSum / Frames):0.0} ms max={Ms(QueueMax):0.0} ms";
    }

    public static bool GlUnavailable { get; private set; }

    /// <summary>Surface vidéo prête (contexte de rendu mpv créé).</summary>
    public static bool IsReady => _ready.Task.IsCompleted;

    private static TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Attend que la surface vidéo soit prête (contexte de rendu créé), au plus <paramref name="timeoutMs"/>.</summary>
    public static Task WaitReadyAsync(int timeoutMs) => Task.WhenAny(_ready.Task, Task.Delay(timeoutMs));

    public MpvVideoView()
    {
        _getProc = (_, name) => _gl?.GetProcAddress(name) ?? IntPtr.Zero;
        _onUpdate = _ =>
        {
            if (Interlocked.Exchange(ref _pendingFrame, 1) == 0)
            {
                Interlocked.Exchange(ref _updateStamp, Stopwatch.GetTimestamp());
                Dispatcher.UIThread.Post(() =>
                {
                    Interlocked.Exchange(ref _requestStamp, Stopwatch.GetTimestamp());
                    RequestNextFrameRendering();
                }, DispatcherPriority.Render);
            }
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlayerProperty && _gl is not null)
        {
            // le contexte de rendu sera recréé au prochain rendu
            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = gl;
        try { Log.Info($"Rendu vidéo : {gl.GetString(0x1F01)} ({gl.GetString(0x1F02)})"); } catch { /* informatif */ }
        // Nouvelle initialisation après une perte de contexte : anciens tampons libérés.
        if (_fboPtr == IntPtr.Zero) _fboPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenGlFbo>());
        if (_flipPtr == IntPtr.Zero) _flipPtr = Marshal.AllocHGlobal(sizeof(int));
        if (_blockPtr == IntPtr.Zero) _blockPtr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(_flipPtr, 1);
        Marshal.WriteInt32(_blockPtr, 0);
        EnsureContext();
    }

    private void EnsureContext()
    {
        var player = Player;
        if (player == _boundPlayer && (_renderCtx != IntPtr.Zero || player is null)) return;
        FreeContext();
        if (player is null || !player.IsInitialized || _gl is null) return;

        _apiTypePtr = Marshal.StringToCoTaskMemUTF8("opengl");
        var init = new MpvOpenGlInitParams
        {
            GetProcAddress = Marshal.GetFunctionPointerForDelegate(_getProc),
            GetProcAddressCtx = IntPtr.Zero
        };
        _initParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenGlInitParams>());
        Marshal.StructureToPtr(init, _initParamsPtr, false);

        var advanced = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(advanced, 0);
        try
        {
            var parameters = new[]
            {
                new MpvRenderParam(MpvRenderParamType.ApiType, _apiTypePtr),
                new MpvRenderParam(MpvRenderParamType.OpenGlInitParams, _initParamsPtr),
                new MpvRenderParam(MpvRenderParamType.Invalid, IntPtr.Zero),
            };
            var r = MpvApi.mpv_render_context_create(out _renderCtx, player.Handle, parameters);
            if (r < 0)
            {
                Log.Error("Création du contexte de rendu mpv impossible : " + MpvApi.ErrorText(r));
                _renderCtx = IntPtr.Zero;
                GlUnavailable = true;
                return;
            }
            MpvApi.mpv_render_context_set_update_callback(_renderCtx, _onUpdate, IntPtr.Zero);
            _boundPlayer = player;
            Log.Info("Contexte de rendu mpv créé");
            _ready.TrySetResult();
            // Un média ouvert avant la création de la surface n'a pas de vidéo : on recharge la piste.
            if (!player.IdleActive) player.CommandAsync("video-reload");
        }
        finally
        {
            Marshal.FreeHGlobal(advanced);
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        Interlocked.Exchange(ref _pendingFrame, 0);
        EnsureContext();

        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        var w = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        var h = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));

        gl.Viewport(0, 0, w, h);
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(GlColorBufferBit);

        if (_renderCtx == IntPtr.Zero) return;

        MpvApi.mpv_render_context_update(_renderCtx);
        var fbo = new MpvOpenGlFbo { Fbo = fb, W = w, H = h, InternalFormat = 0 };
        Marshal.StructureToPtr(fbo, _fboPtr, false);
        var parameters = new[]
        {
            new MpvRenderParam(MpvRenderParamType.OpenGlFbo, _fboPtr),
            new MpvRenderParam(MpvRenderParamType.FlipY, _flipPtr),
            new MpvRenderParam(MpvRenderParamType.BlockForTargetTime, _blockPtr),
            new MpvRenderParam(MpvRenderParamType.Invalid, IntPtr.Zero),
        };
        var t0 = Stopwatch.GetTimestamp();
        var stamp = Interlocked.Exchange(ref _updateStamp, 0);
        MpvApi.mpv_render_context_render(_renderCtx, parameters);
        MpvApi.mpv_render_context_report_swap(_renderCtx);
        if (stamp != 0)
        {
            var lat = t0 - stamp;
            var req = Interlocked.Exchange(ref _requestStamp, 0);
            if (req != 0)
            {
                var queue = req - stamp;
                Timing.QueueSum += queue;
                if (queue > Timing.QueueMax) Timing.QueueMax = queue;
            }
            var draw = Stopwatch.GetTimestamp() - t0;
            Timing.Frames++;
            Timing.RecentMs = Timing.RecentMs <= 0 ? lat * 1000.0 / Stopwatch.Frequency : Timing.RecentMs * 0.9 + lat * 100.0 / Stopwatch.Frequency;
            Timing.LatSum += lat;
            Timing.DrawSum += draw;
            if (lat > Timing.LatMax) Timing.LatMax = lat;
            if (draw > Timing.DrawMax) Timing.DrawMax = draw;
            if (lat > Stopwatch.Frequency / 50) Timing.Over20++;
            if (lat > Stopwatch.Frequency / 25) Timing.Over40++;
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        FreeContext();
        if (_fboPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_fboPtr); _fboPtr = IntPtr.Zero; }
        if (_flipPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_flipPtr); _flipPtr = IntPtr.Zero; }
        if (_blockPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_blockPtr); _blockPtr = IntPtr.Zero; }
        _gl = null;
    }

    protected override void OnOpenGlLost()
    {
        Log.Warn("Contexte OpenGL perdu : le contexte de rendu mpv sera recréé");
        // Le contexte mpv est lié au contexte OpenGL détruit : on le libère pour en recréer un neuf.
        FreeContext();
        base.OnOpenGlLost();
    }

    private void FreeContext()
    {
        if (_renderCtx != IntPtr.Zero)
        {
            if (_ready.Task.IsCompleted) _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            MpvApi.mpv_render_context_set_update_callback(_renderCtx, null, IntPtr.Zero);
            MpvApi.mpv_render_context_free(_renderCtx);
            _renderCtx = IntPtr.Zero;
        }
        _boundPlayer = null;
        if (_apiTypePtr != IntPtr.Zero) { Marshal.FreeCoTaskMem(_apiTypePtr); _apiTypePtr = IntPtr.Zero; }
        if (_initParamsPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_initParamsPtr); _initParamsPtr = IntPtr.Zero; }
    }
}
