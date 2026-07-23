using RemotePointer.Contracts.Coordinates;

namespace RemotePointer.Contracts.Tests.Coordinates;

public sealed class CoordinateMapperTests
{
    private static readonly RectangleD Target = new(-1_920d, -200d, 1_920d, 1_080d);

    public static TheoryData<PointD, NormalizedPoint> Corners => new()
    {
        { new PointD(Target.Left, Target.Top), new NormalizedPoint(0d, 0d) },
        { new PointD(Target.Left + Target.Width, Target.Top), new NormalizedPoint(1d, 0d) },
        { new PointD(Target.Left, Target.Top + Target.Height), new NormalizedPoint(0d, 1d) },
        {
            new PointD(Target.Left + Target.Width, Target.Top + Target.Height),
            new NormalizedPoint(1d, 1d)
        },
    };

    [Theory]
    [MemberData(nameof(Corners))]
    public void Normalize_MapsCornersExactly(PointD point, NormalizedPoint expected)
    {
        var result = CoordinateMapper.Normalize(point, Target);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0d, 0d, 100d, 100d)]
    [InlineData(-3_440d, -1_440d, 3_440d, 1_440d)]
    [InlineData(125.5d, 250.25d, 800.5d, 1_200.75d)]
    public void Normalize_MapsCenterForDifferentlySizedRectangles(
        double left,
        double top,
        double width,
        double height)
    {
        var rectangle = new RectangleD(left, top, width, height);
        var center = new PointD(left + (width / 2d), top + (height / 2d));

        var result = CoordinateMapper.Normalize(center, rectangle);

        Assert.Equal(0.5d, result.X, precision: 12);
        Assert.Equal(0.5d, result.Y, precision: 12);
    }

    [Theory]
    [InlineData(-100d, -100d, 0d, 0d)]
    [InlineData(200d, 200d, 1d, 1d)]
    [InlineData(-1d, 50d, 0d, 0.5d)]
    public void Normalize_ClampsPointsOutsideRectangle(
        double x,
        double y,
        double expectedX,
        double expectedY)
    {
        var result = CoordinateMapper.Normalize(
            new PointD(x, y),
            new RectangleD(0d, 0d, 100d, 100d));

        Assert.Equal(new NormalizedPoint(expectedX, expectedY), result);
    }

    [Fact]
    public void Denormalize_MapsIntoRectangleWithNegativeOrigin()
    {
        var result = CoordinateMapper.Denormalize(new NormalizedPoint(0.25d, 0.75d), Target);

        Assert.Equal(new PointD(-1_440d, 610d), result);
    }

    [Theory]
    [InlineData(double.NaN, 0d)]
    [InlineData(double.PositiveInfinity, 0d)]
    [InlineData(0d, double.NegativeInfinity)]
    public void Normalize_RejectsNonFinitePoints(double x, double y)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CoordinateMapper.Normalize(new PointD(x, y), Target));
    }

    [Theory]
    [InlineData(0d, 100d)]
    [InlineData(-1d, 100d)]
    [InlineData(100d, 0d)]
    [InlineData(100d, double.NaN)]
    public void Normalize_RejectsInvalidRectangles(double width, double height)
    {
        var rectangle = new RectangleD(0d, 0d, width, height);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => CoordinateMapper.Normalize(new PointD(0d, 0d), rectangle));
    }

    [Theory]
    [InlineData(-0.01d, 0.5d)]
    [InlineData(1.01d, 0.5d)]
    [InlineData(double.NaN, 0.5d)]
    public void Denormalize_RejectsInvalidNormalizedPoints(double x, double y)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CoordinateMapper.Denormalize(new NormalizedPoint(x, y), Target));
    }
}
