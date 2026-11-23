using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.Theming;
using MilfTV.ViewModels;
using MilfTV.Views;

namespace MilfTV;

public partial class App : Application
{
    /// <summary>Nettoyage des extractions des anciennes versions (exécutable unique) : seuls les dossiers dont
    /// aucun fichier n'est utilisé (aucune autre copie de MilfTV ouverte) sont supprimés.</summary>
    private static void CleanOldExtractions()
    {
        try
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            // Exécutable unique : …\Temp\.net\MilfTV\<empreinte>\ ; sinon (dossier normal) rien à faire.
            if (current.Parent is not { Name: "MilfTV" } root || root.Parent?.Name != ".net") return;
            foreach (var dir in root.EnumerateDirectories())
            {
                if (string.Equals(dir.FullName, current.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                if (DateTime.UtcNow - dir.LastWriteTimeUtc < TimeSpan.FromDays(2)) continue;
                var probe = Path.Combine(dir.FullName, "libmpv-2.dll");
                try
                {
                    // Fichier verrouillé : cette version est en cours d'utilisation, on n'y touche pas.
                    if (File.Exists(probe)) using (File.Open(probe, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    dir.Delete(true);
                    Log.Info("Ancienne extraction supprimée : " + dir.Name);
                }
                catch { /* en cours d'utilisation ou protégé : laissé */ }
            }
        }
        catch (Exception ex) { Log.Warn("Nettoyage des extractions : " + ex.Message); }
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var firstRun = SettingsStore.IsFirstRun;
        if (!SettingsStore.IsLoaded) SettingsStore.Load();
        Localization.Loc.SetLanguage(SettingsStore.Current.Language);
        SettingsStore.EnsureProfiles();
        if (firstRun)
        {
            // Première utilisation : langues audio et sous-titres selon la langue de l'interface.
            SettingsStore.Current.AudioLanguages = Localization.Loc.DefaultAudioLanguages(Localization.Loc.Lang);
            SettingsStore.Current.SubtitleLanguages = Localization.Loc.DefaultSubtitleLanguages(Localization.Loc.Lang);
        }
        ThemeService.Apply(SettingsStore.Current.ThemeMode, SettingsStore.Current.ColorTheme);
        if (PlatformSettings is { } ps)
            ps.ColorValuesChanged += (_, _) =>
            {
                if (ThemeService.CurrentMode == "System")
                    ThemeService.Apply("System", ThemeService.Current.Id);
            };

        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error("Erreur inattendue dans l'interface", e.Exception);
            e.Handled = true;
            try
            {
                if ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.DataContext is MainViewModel main)
                    main.Toast(Localization.Loc.T("Err.Unexpected"), e.Exception.GetBaseException().Message, true);
            }
            catch { /* on ne relance pas d'erreur depuis le filet de sécurité */ }
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => vm.Shutdown();
            Updates.Downloaded += v => vm.Toast(Localization.Loc.T("Update.ReadyTitle"), Localization.Loc.T("Update.ReadyMsg", v));
            Updates.Start();
        }
        _ = Task.Run(() => Images.ImageLoader.TrimDiskCache());
        _ = Task.Run(CleanOldExtractions);
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Associe automatiquement XxxViewModel → XxxView.</summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Type?> Cache = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Control> Views = new();

    public Control? Build(object? data)
    {
        if (data is null) return null;
        // Les pages principales gardent leur vue (défilement, sélection) entre deux navigations.
        if (data is ViewModelBase { KeepView: true } && Views.TryGetValue(data, out var cached) && cached.Parent is null)
            return cached;
        var view = Create(data);
        if (view is not null && data is ViewModelBase { KeepView: true }) Views.AddOrUpdate(data, view);
        return view;
    }

    private static Control? Create(object data)
    {
        var vmType = data.GetType();
        if (!Cache.TryGetValue(vmType, out var viewType))
        {
            var name = vmType.FullName!.Replace(".ViewModels.", ".Views.").Replace("ViewModel", "View");
            viewType = vmType.Assembly.GetType(name);
            Cache[vmType] = viewType;
        }
        if (viewType is null) return new TextBlock { Text = Localization.Loc.T("Err.ViewMissing", vmType.Name) };
        return (Control)Activator.CreateInstance(viewType)!;
    }

    public bool Match(object? data) => data is ViewModelBase;
}
