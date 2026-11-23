using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MilfTV.Player;

internal enum MpvFormat
{
    None = 0, String = 1, OsdString = 2, Flag = 3, Int64 = 4, Double = 5, Node = 6, NodeArray = 7, NodeMap = 8, ByteArray = 9
}

internal enum MpvEventId
{
    None = 0, Shutdown = 1, LogMessage = 2, GetPropertyReply = 3, SetPropertyReply = 4, CommandReply = 5,
    StartFile = 6, EndFile = 7, FileLoaded = 8, Idle = 11, Tick = 14, ClientMessage = 16, VideoReconfig = 17,
    AudioReconfig = 18, Seek = 20, PlaybackRestart = 21, PropertyChange = 22, QueueOverflow = 24, Hook = 25
}

internal enum MpvEndFileReason { Eof = 0, Stop = 2, Quit = 3, Error = 4, Redirect = 5 }

internal enum MpvRenderParamType
{
    Invalid = 0, ApiType = 1, OpenGlInitParams = 2, OpenGlFbo = 3, FlipY = 4, Depth = 5, IccProfile = 6,
    AmbientLight = 7, X11Display = 8, WlDisplay = 9, AdvancedControl = 10, NextFrameInfo = 11,
    BlockForTargetTime = 12, SkipRendering = 13
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public int Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public IntPtr Prefix;
    public IntPtr Level;
    public IntPtr Text;
    public int LogLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvRenderParam
{
    public MpvRenderParamType Type;
    public IntPtr Data;

    public MpvRenderParam(MpvRenderParamType type, IntPtr data)
    {
        Type = type;
        Data = data;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlInitParams
{
    public IntPtr GetProcAddress;
    public IntPtr GetProcAddressCtx;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlFbo
{
    public int Fbo;
    public int W;
    public int H;
    public int InternalFormat;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr MpvGetProcAddress(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void MpvRenderUpdateCallback(IntPtr ctx);

/// <summary>Liaison P/Invoke minimale vers libmpv-2.dll (API client 2.x + API de rendu OpenGL).</summary>
internal static class MpvApi
{
    private const string Lib = "libmpv-2.dll";

    static MpvApi()
    {
        NativeLibrary.SetDllImportResolver(typeof(MpvApi).Assembly, Resolve);
    }

    /// <summary>libmpv importe vulkan-1.dll (chargeur Vulkan, normalement installé par le pilote graphique). On charge
    /// d'abord celui de Windows : à jour et accordé aux pilotes, il est alors réutilisé par libmpv (une DLL déjà
    /// chargée prime sur la recherche). Sans lui (machine virtuelle, pilote générique ou ancien), la copie livrée avec
    /// MilfTV, à côté de libmpv, prend le relais : sans elle, le moteur vidéo ne démarrerait pas du tout.</summary>
    private static void PreferSystemVulkan()
    {
        // Test uniquement : simule un PC sans Vulkan pour vérifier la copie livrée.
        if (Environment.GetEnvironmentVariable("MILFTV_DATA") is { Length: > 0 }
            && Environment.GetEnvironmentVariable("MILFTV_BUNDLED_VULKAN") == "1") return;
        NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"), out _);
    }

    /// <summary>Emplacement du vulkan-1.dll réellement utilisé (journal), ou null s'il n'est pas chargé.</summary>
    public static string? VulkanPath()
    {
        var h = GetModuleHandleW("vulkan-1.dll");
        if (h == IntPtr.Zero) return null;
        var sb = new System.Text.StringBuilder(520);
        return GetModuleFileNameW(h, sb, sb.Capacity) > 0 ? sb.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetModuleFileNameW(IntPtr module, System.Text.StringBuilder path, int size);

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        PreferSystemVulkan();
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) ?? "" })
        {
            var p = Path.Combine(dir, Lib);
            if (File.Exists(p) && NativeLibrary.TryLoad(p, out var h)) return h;
        }
        return NativeLibrary.TryLoad(Lib, asm, path, out var handle) ? handle : IntPtr.Zero;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mpv_client_api_version(); // unsigned long C = 32 bits sous Windows
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_create();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_terminate_destroy(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_error_string(int error);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_free(IntPtr data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_wakeup(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_request_log_messages(IntPtr ctx, byte[] minLevel);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_property(IntPtr ctx, byte[] name, MpvFormat format, ref double data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_property(IntPtr ctx, byte[] name, MpvFormat format, ref long data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_get_property_string(IntPtr ctx, byte[] name);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_get_property(IntPtr ctx, byte[] name, MpvFormat format, out double data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_get_property(IntPtr ctx, byte[] name, MpvFormat format, out long data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_observe_property(IntPtr ctx, ulong replyUserdata, byte[] name, MpvFormat format);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_command(IntPtr ctx, IntPtr[] args);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_command_async(IntPtr ctx, ulong replyUserdata, IntPtr[] args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_render_context_create(out IntPtr res, IntPtr mpv, MpvRenderParam[] parameters);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_render_context_set_update_callback(IntPtr ctx, MpvRenderUpdateCallback? callback, IntPtr callbackCtx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern ulong mpv_render_context_update(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_render_context_render(IntPtr ctx, MpvRenderParam[] parameters);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_render_context_report_swap(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_render_context_free(IntPtr ctx);

    public static byte[] Utf8(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Array.Resize(ref bytes, bytes.Length + 1); // terminateur nul
        return bytes;
    }

    public static string? FromUtf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    public static string ErrorText(int code) => FromUtf8(mpv_error_string(code)) ?? code.ToString();

    /// <summary>Alloue un tableau argv UTF-8 terminé par NULL. À libérer avec <see cref="FreeArgs"/>.</summary>
    public static IntPtr[] AllocArgs(string[] args)
    {
        var arr = new IntPtr[args.Length + 1];
        for (int i = 0; i < args.Length; i++)
        {
            var bytes = Utf8(args[i]);
            var p = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            arr[i] = p;
        }
        arr[^1] = IntPtr.Zero;
        return arr;
    }

    public static void FreeArgs(IntPtr[] arr)
    {
        foreach (var p in arr)
            if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
    }
}
