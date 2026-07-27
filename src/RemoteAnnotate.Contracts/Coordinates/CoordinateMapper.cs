namespace RemoteAnnotate.Contracts.Coordinates;

public static class CoordinateMapper
{
    public static NormalizedPoint Normalize(PointD point, RectangleD targetRectangle)
    {
        EnsureFinite(point, nameof(point));
        EnsureValid(targetRectangle, nameof(targetRectangle));

        var normalizedX = (point.X - targetRectangle.Left) / targetRectangle.Width;
        var normalizedY = (point.Y - targetRectangle.Top) / targetRectangle.Height;

        return new NormalizedPoint(
            Math.Clamp(normalizedX, 0d, 1d),
            Math.Clamp(normalizedY, 0d, 1d));
    }

    public static PointD Denormalize(NormalizedPoint point, RectangleD targetRectangle)
    {
        EnsureNormalized(point, nameof(point));
        EnsureValid(targetRectangle, nameof(targetRectangle));

        return new PointD(
            targetRectangle.Left + (point.X * targetRectangle.Width),
            targetRectangle.Top + (point.Y * targetRectangle.Height));
    }

    private static void EnsureFinite(PointD point, string parameterName)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Point coordinates must be finite.");
        }
    }

    private static void EnsureNormalized(NormalizedPoint point, string parameterName)
    {
        if (!double.IsFinite(point.X)
            || !double.IsFinite(point.Y)
            || point.X is < 0d or > 1d
            || point.Y is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Normalized coordinates must be finite and between zero and one.");
        }
    }

    private static void EnsureValid(RectangleD rectangle, string parameterName)
    {
        if (!double.IsFinite(rectangle.Left)
            || !double.IsFinite(rectangle.Top)
            || !double.IsFinite(rectangle.Width)
            || !double.IsFinite(rectangle.Height)
            || rectangle.Width <= 0d
            || rectangle.Height <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Rectangle coordinates must be finite and its dimensions must be positive.");
        }
    }
}
