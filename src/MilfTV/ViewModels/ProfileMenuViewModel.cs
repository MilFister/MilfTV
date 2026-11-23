using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Core;
using MilfTV.Data;

namespace MilfTV.ViewModels;

/// <summary>Menu du profil actif (clic sur le profil en bas à gauche) : modifier le profil ou son avatar,
/// ses réglages, passer à un autre profil, en ajouter ou les gérer, se déconnecter.</summary>
public sealed partial class ProfileMenuViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public ProfileMenuViewModel(MainViewModel main)
    {
        _main = main;
        Current = Library.Instance.Profile;
        foreach (var p in SettingsStore.Current.Profiles.Where(p => p.Id != Current?.Id)) Others.Add(p);
    }

    /// <summary>Demande de fermeture du menu (avant d'ouvrir un dialogue ou de changer de profil).</summary>
    public event Action? CloseRequested;

    public override bool KeepView => false;
    public UserProfile? Current { get; }
    public ObservableCollection<UserProfile> Others { get; } = new();
    public bool HasOthers => Others.Count > 0;
    public bool IsKid => Current?.IsKid == true;
    public bool HasPassword => Current?.HasPassword == true;

    [RelayCommand] private void Edit() => Then(() => _ = _main.EditCurrentProfileAsync(openAvatarGallery: false));
    [RelayCommand] private void EditAvatar() => Then(() => _ = _main.EditCurrentProfileAsync(openAvatarGallery: true));
    [RelayCommand] private void Settings() => Then(_main.OpenProfileSettings);
    [RelayCommand] private void Switch(UserProfile p) => Then(() => _ = _main.SelectProfileAsync(p));
    [RelayCommand] private void Add() => Then(() => _ = _main.AddProfileAsync());
    [RelayCommand] private void Manage() => Then(() => _main.ShowProfilePicker("manage"));
    [RelayCommand] private void LogOut() => Then(_main.LogOut);

    private void Then(Action action)
    {
        CloseRequested?.Invoke();
        action();
    }
}
