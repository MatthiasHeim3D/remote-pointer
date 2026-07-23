using RemotePointer.Contracts.Coordinates;

namespace RemotePointer.Client.Services;

public static class TargetRegionGeometry
{
    public const double MinimumWidth = 280d;
    public const double MinimumHeight = 180d;

    public static PointD Resize(
        double currentWidth,
        double currentHeight,
        double horizontalChange,
        double verticalChange,
        double expectedAspectRatio,
        bool lockAspectRatio)
    {
        EnsurePositiveFinite(currentWidth, nameof(currentWidth));
        EnsurePositiveFinite(currentHeight, nameof(currentHeight));
        EnsurePositiveFinite(expectedAspectRatio, nameof(expectedAspectRatio));
        EnsureFinite(horizontalChange, nameof(horizontalChange));
        EnsureFinite(verticalChange, nameof(verticalChange));

        if (!lockAspectRatio)
        {
            return new PointD(
                Math.Max(MinimumWidth, currentWidth + horizontalChange),
                Math.Max(MinimumHeight, currentHeight + verticalChange));
        }

        double width;
        double height;
        if (Math.Abs(horizontalChange) >= Math.Abs(verticalChange * expectedAspectRatio))
        {
            width = Math.Max(MinimumWidth, currentWidth + horizontalChange);
            height = width / expectedAspectRatio;
        }
        else
        {
            height = Math.Max(MinimumHeight, currentHeight + verticalChange);
            width = height * expectedAspectRatio;
        }

        if (height < MinimumHeight)
        {
            height = MinimumHeight;
            width = height * expectedAspectRatio;
        }

        if (width < MinimumWidth)
        {
            width = MinimumWidth;
            height = width / expectedAspectRatio;
        }

        return new PointD(width, height);
    }

    public static double DifferenceFromExpected(
        double width,
        double height,
        double expectedAspectRatio) =>
        AspectRatio.RelativeDifference(
            AspectRatio.Calculate(width, height),
            expectedAspectRatio);

    private static void EnsurePositiveFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must be finite and positive.");
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
