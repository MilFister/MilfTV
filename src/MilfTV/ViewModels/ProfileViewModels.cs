using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.Localization;

namespace MilfTV.ViewModels;

/// <summary>« Qui regarde ? » : choix du profil au lancement, ajout et gestion des profils.</summary>
public sealed partial class ProfilePickerViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public ProfilePickerViewModel(MainViewModel main, bool manage = false)
    {
        _main = main;
        _isManaging = manage;
        Reload();
        // Premier lancement : on ouvre directement la création du profil.
        if (Profiles.Count == 0)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _main.ShowDialog(new ProfileEditorViewModel(_main, null)));
    }

    public bool IsFirstRun => Profiles.Count == 0;

    public override bool KeepView => false;
    public ObservableCollection<UserProfile> Profiles { get; } = new();
    [ObservableProperty] private bool _isManaging;
    /// <summary>Ouvert par « Se déconnecter » : il faut choisir un profil (avec son mot de passe s'il en a un).</summary>
    public bool LoggedOut { get; init; }
    public bool CanClose => !LoggedOut && Library.Instance.Profile is not null && Profiles.Count > 0;
    public string TitleText => IsFirstRun ? Loc.T("Profile.FirstRunTitle")
        : IsManaging ? Loc.T("Profile.ManageTitle") : Loc.T("Profile.WhoIsWatching");

    partial void OnIsManagingChanged(bool value) => OnPropertyChanged(nameof(TitleText));

    public void RefreshTexts() => OnPropertyChanged(nameof(TitleText));

    public void Reload()
    {
        Profiles.Clear();
        foreach (var p in SettingsStore.Current.Profiles) Profiles.Add(p);
        OnPropertyChanged(nameof(IsFirstRun));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(CanClose));
    }

    [RelayCommand]
    private async Task SelectAsync(UserProfile p)
    {
        if (IsManaging)
        {
            // Depuis un profil enfant, gérer les profils demande le code parental.
            if (!await _main.EnsureProfileAdminAsync()) return;
            // Modifier un profil protégé demande son mot de passe.
            if (p.HasPassword && Library.Instance.Profile?.Id != p.Id && !await _main.RequestProfilePasswordAsync(p)) return;
            _main.ShowDialog(new ProfileEditorViewModel(_main, p));
            return;
        }
        await _main.SelectProfileAsync(p);
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        // Un enfant ne peut pas se créer un profil adulte pour contourner le contrôle parental.
        if (await _main.EnsureCanCreateProfileAsync()) _main.ShowDialog(new ProfileEditorViewModel(_main, null));
    }
    [RelayCommand] private void ToggleManage() => IsManaging = !IsManaging;
    [RelayCommand] private void Close() => _main.CloseProfilePicker();
}

/// <summary>Création / modification d'un profil.</summary>
public sealed partial class ProfileEditorViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly UserProfile? _profile;

    public ProfileEditorViewModel(MainViewModel main, UserProfile? profile)
    {
        _main = main;
        _profile = profile;
        _name = profile?.Name ?? "";
        _isKid = profile?.IsKid ?? false;
        _usePassword = profile?.HasPassword ?? false;
        var used = SettingsStore.Current.Profiles.Select(p => p.Color).ToHashSet();
        _color = profile?.Color ?? SettingsStore.ProfileColors.FirstOrDefault(c => !used.Contains(c)) ?? SettingsStore.ProfileColors[0];
        foreach (var c in SettingsStore.ProfileColors) Colors.Add(new ColorChoice { Hex = c, IsSelected = c == _color });
        _avatar = profile?.Avatar;
        AvatarChoices.Add(new AvatarChoice { Owner = this, Value = null });
        if (Controls.ProfileAvatars.FilePath(_avatar) is not null) AvatarChoices.Add(new AvatarChoice { Owner = this, Value = _avatar });
        foreach (var e in Controls.ProfileAvatars.Emojis) AvatarChoices.Add(new AvatarChoice { Owner = this, Value = "emoji:" + e });
        MarkAvatar();
    }

    // ── Avatar : initiale, émoji de la galerie ou image importée ──
    [ObservableProperty] private string? _avatar;
    public ObservableCollection<AvatarChoice> AvatarChoices { get; } = new();
    public bool HasImage => Controls.ProfileAvatars.FilePath(Avatar) is not null;
    partial void OnAvatarChanged(string? value) => OnPropertyChanged(nameof(HasImage));

    [RelayCommand]
    private void PickAvatar(AvatarChoice c)
    {
        Avatar = c.Value;
        MarkAvatar();
    }

    /// <summary>Image importée depuis le disque : elle devient l'avatar et rejoint la galerie (2e position).</summary>
    public void SetImageAvatar(string value)
    {
        var old = AvatarChoices.FirstOrDefault(a => Controls.ProfileAvatars.FilePath(a.Value) is not null);
        if (old is not null) AvatarChoices.Remove(old);
        AvatarChoices.Insert(1, new AvatarChoice { Owner = this, Value = value });
        Avatar = value;
        Error = null;
        MarkAvatar();
    }

    private void MarkAvatar()
    {
        foreach (var a in AvatarChoices) a.IsSelected = a.Value == Avatar;
    }

    public override bool KeepView => false;
    public bool IsNew => _profile is null;
    /// <summary>Ouvrir la galerie d'avatars dès l'affichage de l'éditeur.</summary>
    public bool OpenAvatarGallery { get; set; }
    public string DialogTitle => IsNew ? Loc.T("Profile.AddTitle") : Loc.T("Profile.EditTitle");
    public bool CanDelete => !IsNew && SettingsStore.Current.Profiles.Count > 1;
    public bool CanCancel => SettingsStore.Current.Profiles.Count > 0;
    public bool HadPassword => _profile?.HasPassword == true;

    /// <summary>Profil déjà enfant : seul le code parental permet de retirer ce mode.</summary>
    public bool WasKid => _profile?.IsKid == true;

    [ObservableProperty] private bool _usePassword;
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmPassword = "";
    public ObservableCollection<ColorChoice> Colors { get; } = new();

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isKid;
    [ObservableProperty] private string _color;
    [ObservableProperty] private string? _error;

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Initial));

    [RelayCommand]
    private void PickColor(ColorChoice c)
    {
        Color = c.Hex;
        foreach (var x in Colors) x.IsSelected = x == c;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var name = Name.Trim();
        if (name.Length == 0) { Error = Loc.T("Profile.NameRequired"); return; }
        if (SettingsStore.Current.Profiles.Any(p => p != _profile && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Error = Loc.T("Profile.NameTaken");
            return;
        }
        string? hash = null;
        if (UsePassword)
        {
            if (NewPassword.Length > 0 || !HadPassword)
            {
                if (NewPassword.Length < 4) { Error = Loc.T("Profile.PasswordTooShort"); return; }
                if (NewPassword != ConfirmPassword) { Error = Loc.T("Profile.PasswordMismatch"); return; }
                hash = Secret.HashPassword(NewPassword);
            }
        }

        // Retirer le mode enfant : code parental obligatoire, redemandé même s'il a déjà été saisi.
        if (WasKid && !IsKid && SettingsStore.Current.HasPin && !await _main.RequestPinAsync(forContent: false, force: true))
        {
            IsKid = true; // refusé : l'interrupteur reflète de nouveau le profil réel
            Reopen(Loc.T("Profile.KidPinRequired"));
            return;
        }
        // Activer le mode enfant sans code parental : il faut en créer un, sinon l'enfant pourrait le retirer lui-même.
        if (IsKid && !SettingsStore.Current.HasPin)
        {
            if (!await _main.CreatePinAsync(Loc.T("Pin.ChooseForKid")))
            {
                Reopen(Loc.T("Profile.KidNeedsPin"));
                return;
            }
            _main.Toast(Loc.T("Toast.PinSaved"), Loc.T("Toast.PinSavedMsg"));
        }
        _main.SaveProfile(_profile, name, Color, IsKid, hash, removePassword: !UsePassword, avatar: Avatar);
    }

    /// <summary>Revient à l'éditeur (après un code refusé ou annulé), avec le message d'erreur.</summary>
    private void Reopen(string error)
    {
        Error = error;
        _main.ShowDialog(this);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_profile is null) return;
        // Supprimer un profil enfant (et basculer sur un autre profil) demande aussi le code parental.
        if (_profile.IsKid && SettingsStore.Current.HasPin && !await _main.RequestPinAsync(forContent: false, force: true))
        {
            Reopen(Loc.T("Profile.KidPinRequired"));
            return;
        }
        await _main.DeleteProfileAsync(_profile);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (CanCancel) _main.CloseDialog();
    }
}

/// <summary>Un choix de la galerie d'avatars (null = initiale du profil).</summary>
public sealed partial class AvatarChoice : ObservableObject
{
    public required ProfileEditorViewModel Owner { get; init; }
    public string? Value { get; init; }
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class ColorChoice : ObservableObject
{
    public required string Hex { get; init; }
    [ObservableProperty] private bool _isSelected;
}
