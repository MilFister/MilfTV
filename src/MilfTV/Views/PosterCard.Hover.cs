using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MilfTV.Views;

/// <summary>Affiche : déclenchement de l'aperçu au survol (voir <see cref="PosterPreviewController"/>).</summary>
public partial class PosterCard
{
    /// <summary>Zone de l'image, à côté de laquelle l'aperçu est placé.</summary>
    internal Control PosterArea => PosterBorder;

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        PosterPreviewController.Current?.OnPosterEntered(this, e.GetPosition(TopLevel.GetTopLevel(this)));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        PosterPreviewController.Current?.OnPosterMoved(this, e.GetPosition(TopLevel.GetTopLevel(this)));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        PosterPreviewController.Current?.OnPosterExited(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        PosterPreviewController.Current?.OnPosterDetached(this);
    }
}
