using RemoteAnnotate.Contracts.Coordinates;

namespace RemoteAnnotate.Client.Services;

public sealed class DisplayCoordinateMapper : IDisplayCoordinateMapper
{
    public double PhysicalPixelsToDips(double pixels, double scaleFactor)
    {
        EnsureScaleFactor(scaleFactor);
        EnsureFinite(pixels, nameof(pixels));
        return pixels / scaleFactor;
    }

    public double DipsToPhysicalPixels(double dips, double scaleFactor)
    {
        EnsureScaleFactor(scaleFactor);
        EnsureFinite(dips, nameof(dips));
        return dips * scaleFactor;
    }

    public PointD ToOverlayPoint(
        NormalizedPoint point,
        double overlayWidth,
        double overlayHeight) =>
        CoordinateMapper.Denormalize(
            point,
            new RectangleD(0d, 0d, overlayWidth, overlayHeight));

    private static void EnsureScaleFactor(double scaleFactor)
    {
        if (!double.IsFinite(scaleFactor) || scaleFactor <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scaleFactor),
                "Scale factor must be finite and positive.");
        }
    }

    private static void EnsureFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must be finite.");
        }
    }
}
