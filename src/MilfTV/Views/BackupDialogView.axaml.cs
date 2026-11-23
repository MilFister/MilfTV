using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MilfTV.Core;
using MilfTV.Localization;
using MilfTV.ViewModels;

namespace MilfTV.Views;

/// <summary>Dialogue de sauvegarde / restauration : ouvre les sélecteurs de fichiers puis confie le reste au modèle.</summary>
public partial class BackupDialogView : UserControl
{
    public BackupDialogView() => InitializeComponent();

    private static FilePickerFileType BackupType => new(Loc.T("Backup.FileType")) { Patterns = new[] { "*" + Backup.Extension } };

    protected override async void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Restauration : le choix du fichier vient tout de suite (sauf en test automatisé : pas de fenêtre système).
        if (DataContext is BackupDialogViewModel { IsRestore: true, HasFile: false } && !ViewModels.DevScript.IsEnabled)
        {
            await Task.Delay(150);
            await PickBackupAsync();
        }
    }

    private async void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BackupDialogViewModel vm || !vm.ValidateCreate() || TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Backup.CreateTitle"),
            SuggestedFileName = vm.SuggestedFileName,
            DefaultExtension = Backup.Extension.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { BackupType },
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            if (!path.EndsWith(Backup.Extension, StringComparison.OrdinalIgnoreCase)) path += Backup.Extension;
            await vm.CreateAsync(path);
        }
    }

    private async void OnChooseFileClick(object? sender, RoutedEventArgs e) => await PickBackupAsync();

    private async Task PickBackupAsync()
    {
        if (DataContext is not BackupDialogViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        // Départ dans le dossier des sauvegardes automatiques s'il en contient.
        IStorageFolder? start = null;
        try
        {
            if (Directory.EnumerateFiles(Backup.AutoFolder, "*" + Backup.Extension).Any())
                start = await top.StorageProvider.TryGetFolderFromPathAsync(Backup.AutoFolder);
        }
        catch { /* facultatif */ }
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Backup.RestoreTitle"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = new[] { BackupType, FilePickerFileTypes.All },
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await vm.LoadAsync(path);
    }
}
