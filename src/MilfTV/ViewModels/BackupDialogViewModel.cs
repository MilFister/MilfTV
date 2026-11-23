using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.Localization;

namespace MilfTV.ViewModels;

public enum BackupDialogMode { Create, Restore }

/// <summary>Création ou restauration d'une sauvegarde de la configuration. Les sélecteurs de fichiers sont ouverts
/// par la vue (BackupDialogView), qui appelle ensuite <see cref="CreateAsync"/> ou <see cref="LoadAsync"/>.</summary>
public sealed partial class BackupDialogViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public BackupDialogViewModel(MainViewModel main, BackupDialogMode mode)
    {
        _main = main;
        Mode = mode;
    }

    public override bool KeepView => false;
    public BackupDialogMode Mode { get; }
    public bool IsCreate => Mode == BackupDialogMode.Create;
    public bool IsRestore => Mode == BackupDialogMode.Restore;
    public string DialogTitle => Loc.T(IsCreate ? "Backup.CreateTitle" : "Backup.RestoreTitle");
    public string SuggestedFileName => Backup.SuggestedFileName;

    /// <summary>Des secrets (mots de passe des sources, clé TMDB) seraient sauvegardés : le mot de passe a un sens.</summary>
    public bool HasSecrets =>
        SettingsStore.Current.Sources.Any(s => !string.IsNullOrEmpty(s.PasswordProtected)) || !string.IsNullOrEmpty(SettingsStore.Current.TmdbKeyProtected);

    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _passwordConfirm = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestore))]
    private bool _isBusy;

    // Restauration
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile), nameof(CanRestore))]
    private string? _filePath;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private bool _needsPassword;
    [ObservableProperty] private string? _secretsWarning;
    public bool HasFile => FilePath is not null;
    public bool CanRestore => HasFile && !IsBusy;

    /// <summary>Contrôle avant d'ouvrir le sélecteur « Enregistrer sous ».</summary>
    public bool ValidateCreate()
    {
        Error = Password.Length > 0 && Password != PasswordConfirm ? Loc.T("Backup.PasswordMismatch") : null;
        return Error is null;
    }

    public async Task CreateAsync(string path)
    {
        if (!ValidateCreate()) return;
        IsBusy = true;
        try
        {
            var m = await Backup.CreateAsync(path, Password.Length > 0 ? Password : null);
            _main.CloseDialog();
            _main.Toast(Loc.T("Backup.CreatedTitle"), Path.GetFileName(path) + (m.Secrets == "windows" ? " — " + Loc.T("Backup.CreatedLocalSecrets") : ""));
        }
        catch (Exception ex)
        {
            Log.Warn("Sauvegarde impossible : " + ex.Message);
            Error = Loc.T("Backup.Failed", ex.GetBaseException().Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadAsync(string path)
    {
        Error = null;
        try
        {
            var m = await Task.Run(() => Backup.ReadManifest(path));
            var readable = m.Secrets != "windows" || await Task.Run(() => Backup.WindowsSecretsReadable(path));
            FilePath = path;
            Summary = Loc.T("Backup.Summary", m.Created.LocalDateTime.ToString("g", Loc.Culture), m.App, m.Profiles, m.Sources,
                m.History, m.MyList, m.Favorites, m.Recordings);
            NeedsPassword = m.Secrets == "password";
            SecretsWarning = readable ? null : Loc.T("Backup.SecretsLost");
            Password = "";
        }
        catch (Exception ex)
        {
            FilePath = null;
            Summary = null;
            Error = ex is InvalidDataException ? ex.Message : Loc.T("Backup.ReadFailed", ex.GetBaseException().Message);
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (FilePath is not { } file) return;
        Error = null;
        if (_main.Recorder.ClosingWouldInterrupt)
        {
            Error = Loc.T("Backup.RecordingRunning");
            return;
        }
        IsBusy = true;
        try
        {
            if (NeedsPassword && !await Task.Run(() => Backup.CheckPassword(file, Password)))
            {
                Error = Loc.T("Backup.WrongPassword");
                return;
            }
            // L'état actuel est sauvegardé d'abord : on peut revenir en arrière.
            await Backup.CreateAutomaticAsync("avant-restauration");
            var password = NeedsPassword ? Password : null;
            await Task.Run(() => Backup.PrepareRestore(file, password));
            _main.RestartForRestore();
        }
        catch (Exception ex)
        {
            Log.Warn("Restauration impossible : " + ex.Message);
            Error = Loc.T("Backup.ReadFailed", ex.GetBaseException().Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand] private void Cancel() => _main.CloseDialog();
}
