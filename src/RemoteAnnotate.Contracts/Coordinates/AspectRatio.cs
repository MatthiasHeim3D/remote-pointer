namespace RemoteAnnotate.Contracts.Coordinates;

public static class AspectRatio
{
    public const double DefaultWarningTolerance = 0.02d;

    public static double Calculate(double width, double height)
    {
        if (!double.IsFinite(width)
            || !double.IsFinite(height)
            || width <= 0d
            || height <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Width and height must be finite positive values.");
        }

        return width / height;
    }

    public static double RelativeDifference(double actual, double expected)
    {
        if (!double.IsFinite(actual)
            || !double.IsFinite(expected)
            || actual <= 0d
            || expected <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(actual),
                "Aspect ratios must be finite positive values.");
        }

        return Math.Abs(actual - expected) / expected;
    }

    public static bool ExceedsTolerance(
        double actual,
        double expected,
        double tolerance = DefaultWarningTolerance)
    {
        if (!double.IsFinite(tolerance) || tolerance < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tolerance),
                "Tolerance must be a finite non-negative value.");
        }

        var difference = RelativeDifference(actual, expected);
        if (tolerance == 0d)
        {
            return difference > 0d;
        }

        var comparisonEpsilon = tolerance * 1e-12d;
        return difference - tolerance > comparisonEpsilon;
    }
}
