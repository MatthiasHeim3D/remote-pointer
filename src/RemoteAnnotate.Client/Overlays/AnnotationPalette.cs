using System.Globalization;
using System.Windows.Media;
using RemoteAnnotate.Contracts.Messages;

namespace RemoteAnnotate.Client.Overlays;

/// <summary>
/// Turns the <c>#RRGGBB</c> annotation colour carried by settings and pointer events into the
/// brushes the overlays draw with. Parsing is total: an unparseable value yields the default
/// accent, because a drawing that arrives with a bad colour still has to be shown.
/// </summary>
internal static class AnnotationPalette
{
    /// <summary>Opacity of the wash inside a rectangle or circle.</summary>
    private const byte FillAlpha = 38;

    public static readonly Color DefaultAccent = ToColor(AnnotationColors.Default);

    public static Color ToColor(string? annotationColor)
    {
        var normalized = AnnotationColors.Normalize(annotationColor);
        var channels = int.Parse(
            normalized.AsSpan(1),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);
        return Color.FromRgb(
            (byte)((channels >> 16) & 0xFF),
            (byte)((channels >> 8) & 0xFF),
            (byte)(channels & 0xFF));
    }

    /// <summary>
    /// Encodes channels back into the stored and transmitted <c>#RRGGBB</c> form.
    /// </summary>
    public static string ToAnnotationColor(byte red, byte green, byte blue) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"#{red:X2}{green:X2}{blue:X2}");

    public static SolidColorBrush CreateStrokeBrush(Color accent)
    {
        var brush = new SolidColorBrush(accent);
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush CreateFillBrush(Color accent)
    {
        var brush = new SolidColorBrush(
            Color.FromArgb(FillAlpha, accent.R, accent.G, accent.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// A darkened accent, used where a shape needs an outline against its own fill — the click
    /// marker's white dot, for instance. Scaling the channels keeps the hue, so the outline still
    /// reads as the same annotator's colour.
    /// </summary>
    public static Color Darken(Color accent, double factor) => Color.FromRgb(
        (byte)Math.Clamp(accent.R * factor, 0d, 255d),
        (byte)Math.Clamp(accent.G * factor, 0d, 255d),
        (byte)Math.Clamp(accent.B * factor, 0d, 255d));
}
