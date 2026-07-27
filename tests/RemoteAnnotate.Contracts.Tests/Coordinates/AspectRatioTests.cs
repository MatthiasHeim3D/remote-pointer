using RemoteAnnotate.Contracts.Coordinates;

namespace RemoteAnnotate.Contracts.Tests.Coordinates;

public sealed class AspectRatioTests
{
    [Theory]
    [InlineData(1_920d, 1_080d, 1.7777777777777777d)]
    [InlineData(1_080d, 1_920d, 0.5625d)]
    [InlineData(3_440d, 1_440d, 2.388888888888889d)]
    public void Calculate_ReturnsWidthOverHeight(double width, double height, double expected)
    {
        Assert.Equal(expected, AspectRatio.Calculate(width, height), precision: 12);
    }

    [Fact]
    public void ExceedsTolerance_ReturnsFalseAtExactlyTwoPercent()
    {
        Assert.False(AspectRatio.ExceedsTolerance(1.02d, 1d));
    }

    [Fact]
    public void ExceedsTolerance_ReturnsTrueAboveTwoPercent()
    {
        Assert.True(AspectRatio.ExceedsTolerance(1.02001d, 1d));
    }

    [Theory]
    [InlineData(0d, 1d)]
    [InlineData(1d, 0d)]
    [InlineData(double.PositiveInfinity, 1d)]
    public void Calculate_RejectsInvalidDimensions(double width, double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AspectRatio.Calculate(width, height));
    }
}
