using CommunityToolkit.Mvvm.Input;
using MilfTV.Core;

namespace MilfTV.ViewModels;

/// <summary>Notes de version : les modifications de chaque version, la plus récente en premier.</summary>
public sealed partial class ChangelogDialogViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public ChangelogDialogViewModel(MainViewModel main)
    {
        _main = main;
        Entries = Changelog.Load();
    }

    public override bool KeepView => false;
    public IReadOnlyList<ChangelogEntry> Entries { get; }

    [RelayCommand] private void Close() => _main.CloseDialog();
}
