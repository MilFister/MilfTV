using System;
using Avalonia;
using Avalonia.Controls;
using MilfTV.Core;
using MilfTV.ViewModels;

namespace MilfTV.Views;

/// <summary>Fiche d'une chaîne (contenu de l'infobulle de la liste des chaînes du lecteur). Son contexte est la
/// chaîne ; la fiche crée son modèle à chaque ouverture seulement (les programmes ne sont lus que pour la chaîne
/// survolée, et toujours à jour).</summary>
public partial class ChannelPreviewCard : UserControl
{
    private ChannelPreviewViewModel? _vm;

    public ChannelPreviewCard() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Load();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot is not null) Load();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unload();
        base.OnDetachedFromVisualTree(e);
    }

    private void Load()
    {
        if (DataContext is not LiveChannel ch) { Unload(); return; }
        if (_vm?.Channel == ch) return;
        Unload();
        _vm = new ChannelPreviewViewModel(ch);
        Root.DataContext = _vm;
    }

    private void Unload()
    {
        _vm?.Cancel();
        _vm = null;
        Root.DataContext = null;
    }
}
