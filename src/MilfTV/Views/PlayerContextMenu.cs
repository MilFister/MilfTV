using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MilfTV.Localization;
using MilfTV.ViewModels;

namespace MilfTV.Views;

/// <summary>Menu du clic droit sur la vidéo (lecteur complet et mini-lecteur) : lecture, pistes audio, sous-titres,
/// sortie audio, format d'image, vitesse, enregistrement, informations sur la lecture… Construit à chaque ouverture
/// pour refléter l'état du moment.</summary>
public static class PlayerContextMenu
{
    public static void Show(PlayerViewModel vm, Control target, bool atPointer = true)
    {
        vm.NotifyActivity();
        vm.RefreshAudioDevices();
        var menu = Build(vm);
        if (!atPointer) menu.Placement = Avalonia.Controls.PlacementMode.Center;
        Last = menu;
        menu.Opened += (_, _) => vm.IsMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            vm.IsMenuOpen = false;
            vm.NotifyActivity();
        };
        menu.ShowAt(target, atPointer);
    }

    /// <summary>Dernier menu ouvert (tests MILFTV_DEV : ouvrir un sous-menu pour la capture).</summary>
    internal static MenuFlyout? Last { get; private set; }

    public static MenuFlyout Build(PlayerViewModel vm)
    {
        var menu = new MenuFlyout();
        var items = menu.Items;

        // En-tête : ce qui est en cours de lecture
        items.Add(Header(vm));
        items.Add(new Separator());

        // Lecture
        items.Add(Item(vm.IsPaused ? Loc.T("Ctx.Play") : Loc.T("Ctx.Pause"), vm.TogglePauseCommand, null,
            vm.IsPaused ? "" : "", Loc.T("Key.Space")));
        if (vm.IsLive && !vm.IsAtLiveEdge)
            items.Add(Item(Loc.T("Ui.RetourAuDirect"), vm.GoLiveCommand, null, null, "L", LiveDot()));
        if (!vm.IsLive)
        {
            items.Add(Item(Loc.T("Ctx.Back10"), vm.SeekByCommand, "-10", "", "←"));
            items.Add(Item(Loc.T("Ctx.Fwd30"), vm.SeekByCommand, "30", "", "→"));
        }
        items.Add(new Separator());

        // Son et image
        items.Add(Choices(Loc.T("Ctx.AudioTrack"), "", vm.AudioTracks.Select(t => (t.Label, t.IsSelected, (object?)t)), vm.SelectAudioCommand));
        items.Add(Choices(Loc.T("Ctx.Subtitles"), "", vm.SubtitleTracks.Select(t => (t.Label, t.IsSelected, (object?)t)), vm.SelectSubtitleCommand));
        items.Add(Choices(Loc.T("Settings.AudioOutput"), "", vm.AudioDevices.Select(t => (t.Label, t.IsSelected, (object?)t)), vm.SelectAudioDeviceCommand, Loc.T("Ctx.NoDevice")));
        items.Add(Choices(Loc.T("Ui.FormatDImage"), "",
            PlayerViewModel.AspectChoices.Select(a => (a == "no" ? Loc.T("Player.AspectAuto") : a, a == vm.CurrentAspect, (object?)a)), vm.SetAspectCommand));
        if (!vm.IsLive)
            items.Add(Choices(Loc.T("Ctx.Speed"), "",
                PlayerViewModel.SpeedChoices.Select(s => (PlayerViewModel.SpeedLabel(s), Math.Abs(s - vm.PlaybackSpeed) < 0.001, (object?)s)), vm.SetSpeedCommand));
        items.Add(Item(vm.IsMuted ? Loc.T("Ctx.Unmute") : Loc.T("Ctx.Mute"), vm.ToggleMuteCommand, null, vm.IsMuted ? "" : "", "M"));
        items.Add(new Separator());

        // Selon le contenu
        if (vm.IsLive)
        {
            items.Add(Item(vm.IsRecording ? Loc.T("Ctx.StopRecording") : Loc.T("Ctx.Record"), vm.ToggleRecordCommand, null, "", "R"));
            if (vm.CurrentChannel is { } ch)
                items.Add(Item(ch.IsFavorite ? Loc.T("Ctx.FavRemove") : Loc.T("Ctx.FavAdd"), vm.ToggleFavoriteCommand, null, ch.IsFavorite ? "" : ""));
            if (!vm.IsMini) items.Add(Item(Loc.T("Ctx.Channels"), vm.ToggleSidePanelCommand, null, "", "C"));
        }
        else if (vm.HasEpisodes && !vm.IsMini)
        {
            items.Add(Item(Loc.T("Ui.Episodes"), vm.ToggleSidePanelCommand, null, "", "C"));
        }
        if (!vm.IsMini)
        {
            var info = Item(Loc.T("Ctx.Info"), vm.ToggleStatsCommand, null, "");
            info.ToggleType = MenuItemToggleType.CheckBox;
            info.IsChecked = vm.IsStatsOpen;
            items.Add(info);
        }
        items.Add(new Separator());

        // Affichage
        if (vm.IsMini)
            items.Add(Item(Loc.T("Mini.Expand"), vm.ExpandMiniCommand, null, ""));
        else
        {
            items.Add(Item(Loc.T("Ctx.Mini"), vm.EnterMiniCommand, null, "", "I"));
            items.Add(Item(vm.Main.IsFullscreen ? Loc.T("Ctx.FullscreenOff") : Loc.T("Ctx.FullscreenOn"), vm.Main.ToggleFullscreenCommand, null,
                vm.Main.IsFullscreen ? "" : "", "F"));
        }
        items.Add(Item(Loc.T("Player.StopPlayback"), vm.CloseCommand, null, ""));
        return menu;
    }

    /// <summary>Titre en cours et caractéristiques de l'image (non cliquable).</summary>
    private static MenuItem Header(PlayerViewModel vm)
    {
        var panel = new StackPanel { Spacing = 2, MaxWidth = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = vm.Title,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var details = string.Join("  ·  ", new[] { vm.Subtitle, vm.VideoInfo }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (details.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = details,
                FontSize = 12,
                Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        var item = new MenuItem { Header = panel, Icon = Glyph(vm.IsLive ? "" : ""), IsHitTestVisible = false, Focusable = false };
        return item;
    }

    private static MenuItem Item(string header, ICommand command, object? parameter, string? glyph, string? shortcut = null, Control? icon = null)
    {
        var item = new MenuItem { Header = shortcut is null ? header : WithShortcut(header, shortcut), Command = command, CommandParameter = parameter };
        if (icon is not null) item.Icon = icon;
        else if (glyph is not null) item.Icon = Glyph(glyph);
        return item;
    }

    /// <summary>Libellé + raccourci clavier aligné à droite, dans la langue de l'application (« Espace », pas « Space »).</summary>
    private static Control WithShortcut(string header, string shortcut)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinWidth = 230 };
        grid.Children.Add(new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center });
        var key = new TextBlock { Text = shortcut, Opacity = 0.55, FontSize = 12, Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(key, 1);
        grid.Children.Add(key);
        return grid;
    }

    /// <summary>Sous-menu à choix unique (pistes, sorties, formats, vitesses).</summary>
    private static MenuItem Choices(string header, string glyph, IEnumerable<(string Label, bool Selected, object? Value)> choices, ICommand command, string? emptyText = null)
    {
        var parent = new MenuItem { Header = header, Icon = Glyph(glyph) };
        foreach (var (label, selected, value) in choices)
            parent.Items.Add(new MenuItem
            {
                Header = label,
                Command = command,
                CommandParameter = value,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = selected,
            });
        if (parent.Items.Count == 0)
            parent.Items.Add(new MenuItem { Header = emptyText ?? Loc.T("Ctx.NoTrack"), IsEnabled = false });
        return parent;
    }

    private static TextBlock Glyph(string glyph)
    {
        var t = new TextBlock { Text = glyph, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        t.Classes.Add("icon");
        return t;
    }

    private static Control LiveDot() => new Ellipse
    {
        Width = 9,
        Height = 9,
        Fill = new SolidColorBrush(Color.Parse("#E5484D")),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
