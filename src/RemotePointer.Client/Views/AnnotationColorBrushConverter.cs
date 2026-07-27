using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using RemotePointer.Client.Overlays;

namespace RemotePointer.Client.Views;

/// <summary>
/// Turns a stored <c>#RRGGBB</c> annotation colour into the brush a swatch is painted with.
/// The same parsing the overlays use, so what the settings pane shows is exactly what gets
/// drawn — including the fallback when the stored value is unusable.
/// </summary>
public sealed class AnnotationColorBrushConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        _ = targetType;
        _ = parameter;
        _ = culture;
        var brush = new SolidColorBrush(AnnotationPalette.ToColor(value as string));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) => throw new NotSupportedException();
}
