using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MilfTV.ViewModels;

namespace MilfTV.Views;

public partial class WelcomeView : UserControl
{
    public WelcomeView() => InitializeComponent();
}

public partial class SourceEditorView : UserControl
{
    public SourceEditorView() => InitializeComponent();

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SourceEditorViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = MilfTV.Localization.Loc.T("Source.PickM3uTitle"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(MilfTV.Localization.Loc.T("Source.PlaylistFiles")) { Patterns = new[] { "*.m3u", "*.m3u8", "*.txt" } },
                FilePickerFileTypes.All
            }
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.Url = path;
    }
}
