using Avalonia.Data.Converters;
using MilfTV.Localization;

namespace MilfTV.Controls;

public static class PlayerConv
{
    public static readonly IValueConverter VisibleOpacity = new FuncValueConverter<bool, double>(b => b ? 1.0 : 0.0);
    public static readonly IValueConverter PanelTitle = new FuncValueConverter<bool, string>(live => live ? Loc.T("Common.Channels") : Loc.T("Common.Episodes"));
    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(n => n == 0);
}
