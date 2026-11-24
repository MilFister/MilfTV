using Avalonia;
using MilfTV.Core;

namespace MilfTV;

/// <summary>Envoie les avertissements et erreurs d'Avalonia dans le journal de l'application
/// (les erreurs de liaison, très bavardes, seulement à partir du niveau Erreur).</summary>
internal sealed class AvaloniaFileLogSink : Avalonia.Logging.ILogSink
{
    public bool IsEnabled(Avalonia.Logging.LogEventLevel level, string area) =>
        level >= Avalonia.Logging.LogEventLevel.Error
        || (level >= Avalonia.Logging.LogEventLevel.Warning && area is not (Avalonia.Logging.LogArea.Binding or Avalonia.Logging.LogArea.Property));

    public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate) =>
        Write(level, area, messageTemplate, Array.Empty<object?>());

    public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
        Write(level, area, messageTemplate, propertyValues);

    private static void Write(Avalonia.Logging.LogEventLevel level, string area, string template, object?[] values)
    {
        // Gabarit « {Nom} » : on remplace les jetons dans l'ordre par les valeurs.
        var i = 0;
        var text = System.Text.RegularExpressions.Regex.Replace(template, @"\{[^}]+\}", _ => i < values.Length ? values[i++]?.ToString() ?? "null" : "?");
        var line = $"Avalonia [{area}] {text}";
        if (level >= Avalonia.Logging.LogEventLevel.Error) Core.Log.Error(line);
        else Core.Log.Warn(line);
    }
}

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Programme d'installation / mises à jour (Velopack) : doit passer en premier. Lors d'une installation,
        // d'une mise à jour ou d'une désinstallation, il traite l'étape demandée puis termine le processus.
        // Pas d'installation automatique au démarrage d'une version téléchargée : MilfTV l'installe à la fermeture,
        // seulement si elle correspond au canal choisi (stable ou bêta).
        // Raccourci du bureau : proposé au premier lancement après l'installation, retiré à la désinstallation.
        try
        {
            Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false)
                .OnFirstRun(_ => DesktopShortcut.FirstRunAfterInstall = true)
                .OnBeforeUninstallFastCallback(_ => DesktopShortcut.RemoveOnUninstall())
                .Run();
        }
        catch (Exception ex) { Log.Warn("Velopack : " + ex.Message); }
        // Redémarrage après une restauration : on attend la fin de l'instance précédente.
        Backup.WaitForPreviousInstance(args);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Exception non gérée", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn("Tâche non observée : " + e.Exception.GetBaseException().Message);
            e.SetObserved();
        };
        Log.Info($"Démarrage de MilfTV {AppVersion.Display}");
        // Restauration d'une sauvegarde préparée avant le redémarrage : avant toute lecture des réglages et données.
        Backup.ApplyPendingRestore();
        // Lus dès maintenant : le mode de composition de la fenêtre se choisit avant son ouverture.
        Data.SettingsStore.Load();
        CheckRenderGuard(args);

        if (args.Length >= 2 && args[0] == "--make-icon")
        {
            Controls.IconMakerImpl.Run(args[1]);
            return;
        }

        var mutexName = AppPaths.DevRoot is null
            ? "MilfTV.SingleInstance"
            : "MilfTV.SingleInstance.Dev." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.DevRoot)))[..12];
        using var mutex = new Mutex(true, mutexName, out var firstInstance);
        if (!firstInstance)
        {
            Log.Info("Une instance est déjà ouverte : fermeture.");
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("Arrêt sur erreur fatale", ex);
            throw;
        }
    }

    /// <summary>Mode de composition de la fenêtre.
    /// « Affichage fluide » (par défaut) : swapchain DXGI à faible latence, l'interface et la vidéo suivent la fréquence de l'écran.
    /// Mesuré sur un PC 4K 120 Hz / 240 Hz : environ 22 images/s en composition WinUI (images vidéo perdues en continu),
    /// plus de 80 images/s et aucune image perdue en DXGI. Le mode « compatibilité » garde le choix par défaut d'Avalonia.
    /// MILFTV_COMPOSITION (tests) force un mode : dxgi, winui, dcomp, redirect.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    private const int SmRemoteSession = 0x1000;

    /// <summary>Témoin « fenêtre pas encore affichée » : présent au lancement suivant = le mode fluide a échoué.</summary>
    public static string RenderGuardPath => Path.Combine(AppPaths.Local, "render-pending.flag");
    public static bool SafeMode { get; private set; }

    private static Win32CompositionMode[] CompositionModes()
    {
        var compat = new[] { Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface };
        var smooth = new[] { Win32CompositionMode.LowLatencyDxgiSwapChain, Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface };
        var mode = (ViewModels.DevScript.ToolsCompiled ? Environment.GetEnvironmentVariable("MILFTV_COMPOSITION") : null) switch
        {
            "dxgi" => smooth,
            "winui" => compat,
            "dcomp" => new[] { Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface },
            "redirect" => new[] { Win32CompositionMode.RedirectionSurface },
            _ => Data.SettingsStore.Current.SmoothRendering && !SafeMode && !IsRemoteSession() ? smooth : compat,
        };
        try
        {
            if (ReferenceEquals(mode, smooth)) File.WriteAllText(RenderGuardPath, DateTime.Now.ToString("O"));
            else File.Delete(RenderGuardPath);
        }
        catch { /* témoin facultatif */ }
        Log.Info("Composition de la fenêtre : " + string.Join(" > ", mode));
        return mode;
    }

    private static bool IsRemoteSession()
    {
        try { return GetSystemMetrics(SmRemoteSession) != 0; }
        catch { return false; }
    }

    /// <summary>Au démarrage : mode sans échec demandé, ou fenêtre jamais affichée au lancement précédent.</summary>
    private static void CheckRenderGuard(string[] args)
    {
        SafeMode = args.Any(a => a.Equals("--safe-mode", StringComparison.OrdinalIgnoreCase));
        if (!File.Exists(RenderGuardPath) || !Data.SettingsStore.Current.SmoothRendering) return;
        Log.Warn("La fenêtre ne s'est pas affichée au lancement précédent : affichage fluide désactivé (mode compatible)");
        Data.SettingsStore.Current.SmoothRendering = false;
        Data.SettingsStore.SaveNow();
        RenderFallbackHappened = true;
    }

    /// <summary>Le mode fluide a été désactivé automatiquement à ce lancement (message à l'utilisateur).</summary>
    public static bool RenderFallbackHappened { get; private set; }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Wgl, Win32RenderingMode.Software },
                CompositionMode = CompositionModes(),
            })
            .AfterSetup(_ => Avalonia.Logging.Logger.Sink = new AvaloniaFileLogSink());
}
