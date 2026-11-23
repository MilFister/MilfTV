using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MilfTV.Core;
using MilfTV.ViewModels;

namespace MilfTV.Views;

public partial class GuideView : UserControl
{
    public GuideView()
    {
        InitializeComponent();
        Grid.ProgramClicked += (ch, p) => (DataContext as GuideViewModel)?.Select(ch, p);
        Grid.ProgramActivated += (ch, p) => _ = (DataContext as GuideViewModel)?.ActivateAsync(ch, p);
    }
}

public partial class MovieDetailView : UserControl
{
    public MovieDetailView() => InitializeComponent();
}

public partial class SeriesDetailView : UserControl
{
    public SeriesDetailView() => InitializeComponent();
}

public partial class FavoritesView : UserControl
{
    public FavoritesView() => InitializeComponent();
}

public partial class RecordingsView : UserControl
{
    public RecordingsView() => InitializeComponent();
}

public partial class SearchView : UserControl
{
    public SearchView() => InitializeComponent();
}

public partial class PinDialogView : UserControl
{
    public PinDialogView() => InitializeComponent();
}

public partial class ConfirmDialogView : UserControl
{
    public ConfirmDialogView() => InitializeComponent();
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Changement de section : on revient en haut du contenu.
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnVmPropertyChanged;
        _subscribed = Vm;
        if (_subscribed is not null) _subscribed.PropertyChanged += OnVmPropertyChanged;
    }

    private SettingsViewModel? _subscribed;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs a)
    {
        if (a.PropertyName == nameof(SettingsViewModel.Section) && this.FindControl<ScrollViewer>("ContentScroll") is { } sv)
            sv.Offset = default;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnVmPropertyChanged;
        _subscribed = null;
    }

    private void OnPosterSize0(object? s, RoutedEventArgs e) { if (Vm is { } vm) vm.PosterSize = 0; }
    private void OnPosterSize1(object? s, RoutedEventArgs e) { if (Vm is { } vm) vm.PosterSize = 1; }
    private void OnPosterSize2(object? s, RoutedEventArgs e) { if (Vm is { } vm) vm.PosterSize = 2; }

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = MilfTV.Localization.Loc.T("Common.Folder"),
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) vm.RecordingsFolder = path;
    }
}

public partial class ProfilePickerView : UserControl
{
    // Changement de langue pendant que l'écran est affiché : le titre (calculé) suit.
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MilfTV.Localization.Loc.LanguageChanged += OnLanguageChanged;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        MilfTV.Localization.Loc.LanguageChanged -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLanguageChanged() => (DataContext as ProfilePickerViewModel)?.RefreshTexts();

    public ProfilePickerView()
    {
        InitializeComponent();
        PointerPressed += (_, e) =>
        {
            if (e.GetPosition(this).Y < 48 && TopLevel.GetTopLevel(this) is Window w && w.WindowState != WindowState.FullScreen
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount == 2) w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else w.BeginMoveDrag(e);
            }
        };
    }
}

public partial class ProfileEditorView : UserControl
{
    public ProfileEditorView()
    {
        InitializeComponent();
        // « Changer l'avatar » depuis le menu du profil : la galerie s'ouvre d'emblée.
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is ProfileEditorViewModel { OpenAvatarGallery: true } vm)
            {
                vm.OpenAvatarGallery = false;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => AvatarButton.Flyout?.ShowAt(AvatarButton), Avalonia.Threading.DispatcherPriority.Background);
            }
        };
    }

    private void OnChangeAvatarClick(object? sender, RoutedEventArgs e) => AvatarButton.Flyout?.ShowAt(AvatarButton);

    /// <summary>Tests (MILFTV_DEV) : ouvre la galerie d'avatars pour la capture.</summary>
    internal void DevOpenAvatarFlyout() => AvatarButton.Flyout?.ShowAt(AvatarButton);

    /// <summary>Image d'avatar choisie sur le disque : recadrée en carré et réduite, puis proposée dans la galerie.</summary>
    private async void OnPickAvatarImage(object? sender, RoutedEventArgs e)
    {
        AvatarButton.Flyout?.Hide();
        if (DataContext is not ProfileEditorViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = MilfTV.Localization.Loc.T("Profile.PickImage"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(MilfTV.Localization.Loc.T("Profile.ImageFiles")) { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" } },
            },
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            if (MilfTV.Controls.ProfileAvatars.Import(stream) is { } value) vm.SetImageAvatar(value);
            else vm.Error = MilfTV.Localization.Loc.T("Profile.ImageError");
        }
        catch (Exception ex)
        {
            Log.Warn("Image d'avatar illisible : " + ex.Message);
            vm.Error = MilfTV.Localization.Loc.T("Profile.ImageError");
        }
    }
}

public partial class ProfileMenuView : UserControl
{
    public ProfileMenuView() => InitializeComponent();
}

public partial class PasswordDialogView : UserControl
{
    public PasswordDialogView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => PasswordBox.Focus());
    }
}
