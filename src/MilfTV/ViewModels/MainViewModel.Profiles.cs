using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.Localization;
using MilfTV.Theming;

namespace MilfTV.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Écran « Qui regarde ? » affiché par-dessus l'application (null = masqué).</summary>
    [ObservableProperty] private ProfilePickerViewModel? _profileOverlay;

    /// <summary>Le profil actif a été déverrouillé (mot de passe saisi) pendant cette session.</summary>
    private bool _currentProfileVerified;
    /// <summary>Changement de profil en cours (la langue peut changer : pages reconstruites une seule fois).</summary>
    private bool _profileSwitching;

    /// <summary>Démarrage : création du premier profil, choix du profil ou ouverture directe du dernier.</summary>
    private void StartWithProfiles()
    {
        var s = SettingsStore.Current;
        AdoptLegacySources();
        Controls.ProfileAvatars.CleanupUnused();
        var last = s.Profiles.FirstOrDefault(p => p.Id == s.LastProfileId) ?? s.Profiles.FirstOrDefault();
        if (last is null || s.AskProfileAtStartup || last.HasPassword)
        {
            CurrentPage = null;
            ProfileOverlay = new ProfilePickerViewModel(this);
        }
        else
        {
            _ = SelectProfileAsync(last);
        }
    }

    /// <summary>Les sources créées avant les profils sont rattachées au premier profil et partagées.</summary>
    private static void AdoptLegacySources()
    {
        var s = SettingsStore.Current;
        if (s.Profiles.Count == 0) return;
        var changed = false;
        foreach (var src in s.Sources.Where(x => x.OwnerProfileId is null))
        {
            src.OwnerProfileId = s.Profiles[0].Id;
            src.IsShared = true;
            changed = true;
        }
        if (changed) SettingsStore.Save();
    }

    public void RefreshSources()
    {
        Sources.Clear();
        foreach (var src in SettingsStore.SourcesFor(Lib.Profile)) Sources.Add(src);
    }

    [RelayCommand]
    public void ShowProfilePicker(string? manage = null)
    {
        IsSearchOpen = false;
        if (Player.IsOpen) Player.Close();
        ProfileOverlay = new ProfilePickerViewModel(this, manage == "manage");
    }

    public void CloseProfilePicker()
    {
        if (ProfileOverlay is null or { CanClose: true }) ProfileOverlay = null;
    }

    [RelayCommand]
    private Task SwitchProfileAsync(UserProfile p) => SelectProfileAsync(p);

    // ───────────── Menu du profil (bas de la barre latérale) ─────────────

    /// <summary>Modifier le profil actif (code parental depuis un profil enfant), éventuellement en ouvrant la galerie d'avatars.</summary>
    public async Task EditCurrentProfileAsync(bool openAvatarGallery)
    {
        if (Lib.Profile is not { } p || !await EnsureProfileAdminAsync()) return;
        ShowDialog(new ProfileEditorViewModel(this, p) { OpenAvatarGallery = openAvatarGallery });
    }

    public async Task AddProfileAsync()
    {
        if (await EnsureCanCreateProfileAsync()) ShowDialog(new ProfileEditorViewModel(this, null));
    }

    /// <summary>Paramètres, section « Mon profil ».</summary>
    public void OpenProfileSettings()
    {
        Navigate("settings");
        if (CurrentPage is SettingsViewModel s) s.SelectSectionCommand.Execute("profile");
    }

    /// <summary>Se déconnecter : la lecture s'arrête et l'écran « Qui regarde ? » s'affiche sans pouvoir être fermé.
    /// Revenir sur un profil protégé redemande son mot de passe ; le code parental est reverrouillé.</summary>
    [RelayCommand]
    public void LogOut()
    {
        Log.Info($"Déconnexion du profil « {Lib.Profile?.Name} »");
        if (Player.IsOpen) Player.Close();
        if (IsTrailerOpen) CloseTrailer();
        IsSearchOpen = false;
        Dialog = null;
        _currentProfileVerified = false;
        Lib.ParentalUnlocked = false;
        Lib.RefreshParental();
        ProfileOverlay = new ProfilePickerViewModel(this) { LoggedOut = true };
    }

    /// <summary>Demande le mot de passe d'un profil protégé. Vrai si correct.</summary>
    public Task<bool> RequestProfilePasswordAsync(UserProfile p)
    {
        if (!p.HasPassword) return Task.FromResult(true);
        var tcs = new TaskCompletionSource<bool>();
        Dialog = new PasswordDialogViewModel(p, ok =>
        {
            Dialog = null;
            tcs.TrySetResult(ok);
        });
        return tcs.Task;
    }

    /// <summary>Active un profil (après son mot de passe s'il en a un), sa source, son apparence, puis son tableau de bord.</summary>
    public async Task SelectProfileAsync(UserProfile p, bool skipPassword = false)
    {
        var same = Lib.Profile?.Id == p.Id;
        if (!skipPassword && p.HasPassword && !(same && _currentProfileVerified))
        {
            if (!await RequestProfilePasswordAsync(p)) return;
        }
        _currentProfileVerified = true;
        if (Player.IsOpen) Player.Close();
        if (!same) Lib.ParentalUnlocked = false;

        await Lib.SetProfileAsync(p);
        ProfileOverlay = null;
        _profileSwitching = true;
        try { ApplyProfileAppearance(p); }
        finally { _profileSwitching = false; }
        var freeAdded = AddFreeSourceIfNone(p);
        RefreshSources();

        var visible = SettingsStore.SourcesFor(p).ToList();
        var src = visible.FirstOrDefault(s => s.Id == p.ActiveSourceId) ?? visible.FirstOrDefault();
        DropPages();
        if (src is null)
        {
            if (Lib.Source is not null) await Lib.ActivateAsync(null);
            SetPage(new WelcomeViewModel(this), clearStack: true);
        }
        else
        {
            Navigate("home");
            if (freeAdded) Toast(Loc.T("Toast.FreeSourceAdded"), Loc.T("Toast.FreeSourceMsg"));
            if (Lib.Source?.Id != src.Id) await Lib.ActivateAsync(src);
        }
        OnPropertyChanged(nameof(Lib));
    }

    /// <summary>Playlist gratuite donnée aux profils sans source : chaînes françaises en accès libre (projet iptv-org).</summary>
    public const string FreeSourceUrl = "https://iptv-org.github.io/iptv/countries/fr.m3u";

    /// <summary>Un profil qui ne voit aucune source (ni à lui, ni partagée) reçoit la playlist gratuite, privée,
    /// pour avoir des chaînes tout de suite. Il peut la supprimer ou en ajouter d'autres ensuite.</summary>
    private static bool AddFreeSourceIfNone(UserProfile p)
    {
        if (SettingsStore.SourcesFor(p).Any()) return false;
        var src = new SourceConfig
        {
            Name = Loc.T("Source.FreeName"),
            Kind = SourceKind.M3u,
            Url = FreeSourceUrl,
            OwnerProfileId = p.Id,
            IsShared = false,
        };
        SettingsStore.Current.Sources.Add(src);
        p.ActiveSourceId = src.Id;
        SettingsStore.Save();
        Log.Info($"Profil « {p.Name} » sans source : playlist gratuite ajoutée ({FreeSourceUrl})");
        return true;
    }

    /// <summary>Thème, couleur et langue propres au profil (sinon réglages généraux).</summary>
    public static void ApplyProfileAppearance(UserProfile p)
    {
        var s = SettingsStore.Current;
        // Un profil sans choix propre adopte les réglages actuels : il ne suivra plus ceux des autres profils.
        p.Prefs.ThemeMode ??= s.ThemeMode;
        p.Prefs.ColorTheme ??= s.ColorTheme;
        p.Prefs.Language ??= s.Language;
        // Les réglages généraux suivent le dernier profil utilisé (écran « Qui regarde ? » au lancement).
        s.ThemeMode = p.Prefs.ThemeMode;
        s.ColorTheme = p.Prefs.ColorTheme;
        s.Language = p.Prefs.Language;
        SettingsStore.Save();
        ThemeService.Apply(p.Prefs.ThemeMode, p.Prefs.ColorTheme);
        var lang = p.Prefs.Language;
        var resolved = string.IsNullOrEmpty(lang) || lang == "auto" ? Loc.SystemLanguage() : lang;
        if (resolved != Loc.Lang) Loc.SetLanguage(lang);
    }

    /// <summary>Crée (profile null) ou modifie un profil.</summary>
    public async void SaveProfile(UserProfile? profile, string name, string color, bool isKid, string? newPasswordHash, bool removePassword, string? avatar = null)
    {
        try { await SaveProfileCoreAsync(profile, name, color, isKid, newPasswordHash, removePassword, avatar); }
        catch (Exception ex)
        {
            Log.Error("Enregistrement du profil", ex);
            Toast(Loc.T("Err.Unexpected"), ex.Message, true);
        }
    }

    private async Task SaveProfileCoreAsync(UserProfile? profile, string name, string color, bool isKid, string? newPasswordHash, bool removePassword, string? avatar)
    {
        var s = SettingsStore.Current;
        var isNew = profile is null;
        var isFirst = s.Profiles.Count == 0;
        profile ??= new UserProfile();
        profile.Name = name;
        profile.Color = color;
        profile.IsKid = isKid;
        // Image d'avatar remplacée ou retirée : l'ancien fichier ne sert plus.
        if (profile.Avatar != avatar) Controls.ProfileAvatars.DeleteFile(profile.Avatar);
        profile.Avatar = avatar;
        if (newPasswordHash is not null) profile.PasswordHash = newPasswordHash;
        else if (removePassword) profile.PasswordHash = null;
        if (isNew) s.Profiles.Add(profile);
        SettingsStore.Save();
        CloseDialog();
        ProfileOverlay?.Reload();

        if (isFirst)
        {
            // Premier profil : il récupère l'historique et les sources existants (ancienne version).
            UserDataStore.MigrationProfileId = profile.Id;
            try { UserDataStore.AdoptOrphanData(profile.Id); } catch (Exception ex) { Log.Warn("Reprise de l'historique : " + ex.Message); }
            AdoptLegacySources();
            await SelectProfileAsync(profile, skipPassword: true);
            return;
        }
        if (Lib.Profile?.Id == profile.Id)
        {
            Lib.Profile = null;
            Lib.Profile = profile;
            Lib.RefreshParental();
        }
        Toast(isNew ? Loc.T("Profile.Created") : Loc.T("Profile.Saved"), name);
    }

    public async Task DeleteProfileAsync(UserProfile p)
    {
        var s = SettingsStore.Current;
        if (s.Profiles.Count <= 1) return;
        if (!await ConfirmAsync(Loc.T("Profile.DeleteTitle"), Loc.T("Profile.DeleteMessage", p.Name), Loc.T("Common.Delete")))
            return;
        s.Profiles.RemoveAll(x => x.Id == p.Id);
        Controls.ProfileAvatars.DeleteFile(p.Avatar);
        // Ses sources privées disparaissent ; ses sources partagées passent au premier profil restant.
        foreach (var src in s.Sources.Where(x => x.OwnerProfileId == p.Id).ToList())
        {
            if (src.IsShared) src.OwnerProfileId = s.Profiles[0].Id;
            else
            {
                s.Sources.Remove(src);
                Lib.ForgetSource(src);
            }
        }
        SettingsStore.Save();
        try { UserDataStore.DeleteProfile(p.Id); } catch (Exception ex) { Log.Warn("Suppression du profil : " + ex.Message); }
        ProfileOverlay?.Reload();
        Toast(Loc.T("Profile.Deleted"), p.Name);
        if (Lib.Profile?.Id == p.Id)
        {
            Lib.Profile = null;
            ShowProfilePicker();
        }
    }
}

/// <summary>Saisie du mot de passe d'un profil protégé.</summary>
public sealed partial class PasswordDialogViewModel : ViewModelBase
{
    private readonly UserProfile _profile;
    private readonly Action<bool> _done;

    public PasswordDialogViewModel(UserProfile profile, Action<bool> done)
    {
        _profile = profile;
        _done = done;
    }

    public override bool KeepView => false;
    public UserProfile Profile => _profile;
    public string Prompt => Loc.T("Profile.PasswordFor", _profile.Name);
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string? _error;

    [RelayCommand]
    private void Confirm()
    {
        if (Secret.VerifyPassword(Password, _profile.PasswordHash)) _done(true);
        else
        {
            Error = Loc.T("Profile.PasswordWrong");
            Password = "";
        }
    }

    [RelayCommand] private void Cancel() => _done(false);
}
