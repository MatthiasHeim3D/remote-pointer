using RemotePointer.Client.Services;

namespace RemotePointer.Client.Tests.Services;

public sealed class TargetRegionGeometryTests
{
    [Fact]
    public void Resize_ChangesDimensionsIndependentlyWhenUnlocked()
    {
        var result = TargetRegionGeometry.Resize(
            640d,
            360d,
            horizontalChange: 100d,
            verticalChange: 50d,
            expectedAspectRatio: 16d / 9d,
            lockAspectRatio: false);

        Assert.Equal(740d, result.X);
        Assert.Equal(410d, result.Y);
    }

    [Fact]
    public void Resize_PreservesExpectedAspectRatioFromHorizontalDrag()
    {
        var result = TargetRegionGeometry.Resize(
            640d,
            360d,
            horizontalChange: 160d,
            verticalChange: 5d,
            expectedAspectRatio: 16d / 9d,
            lockAspectRatio: true);

        Assert.Equal(800d, result.X, precision: 12);
        Assert.Equal(450d, result.Y, precision: 12);
    }

    [Fact]
    public void Resize_PreservesExpectedAspectRatioFromVerticalDrag()
    {
        var result = TargetRegionGeometry.Resize(
            640d,
            360d,
            horizontalChange: 2d,
            verticalChange: 90d,
            expectedAspectRatio: 16d / 9d,
            lockAspectRatio: true);

        Assert.Equal(800d, result.X, precision: 12);
        Assert.Equal(450d, result.Y, precision: 12);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resize_EnforcesMinimumDimensions(bool lockAspectRatio)
    {
        var result = TargetRegionGeometry.Resize(
            300d,
            200d,
            horizontalChange: -1_000d,
            verticalChange: -1_000d,
            expectedAspectRatio: 16d / 9d,
            lockAspectRatio);

        Assert.True(result.X >= TargetRegionGeometry.MinimumWidth);
        Assert.True(result.Y >= TargetRegionGeometry.MinimumHeight);
    }

    [Fact]
    public void DifferenceFromExpected_ReturnsZeroForMatchingShape()
    {
        var result = TargetRegionGeometry.DifferenceFromExpected(1_920d, 1_080d, 16d / 9d);

        Assert.Equal(0d, result, precision: 12);
    }

    [Fact]
    public void FitWithin_CentersLargestMatchingLandscapeRectangle()
    {
        var result = TargetRegionGeometry.FitWithin(
            new(-1_920d, 0d, 1_920d, 1_200d),
            expectedAspectRatio: 16d / 9d,
            lockAspectRatio: true);

        Assert.Equal(-1_920d, result.Left, precision: 12);
        Assert.Equal(60d, result.Top, precision: 12);
        Assert.Equal(1_920d, result.Width, precision: 12);
        Assert.Equal(1_080d, result.Height, precision: 12);
    }

    [Fact]
    public void FitWithin_CentersLargestMatchingPortraitRectangle()
    {
        var result = TargetRegionGeometry.FitWithin(
            new(0d, -200d, 1_920d, 1_080d),
            expectedAspectRatio: 9d / 16d,
            lockAspectRatio: true);

        Assert.Equal(656.25d, result.Left, precision: 12);
        Assert.Equal(-200d, result.Top, precision: 12);
        Assert.Equal(607.5d, result.Width, precision: 12);
        Assert.Equal(1_080d, result.Height, precision: 12);
    }

    [Fact]
    public void FitWithin_FillsBoundsWhenAspectRatioIsUnlocked()
    {
        var bounds = new RemotePointer.Contracts.Coordinates.RectangleD(-500d, 100d, 2_560d, 1_440d);

        var result = TargetRegionGeometry.FitWithin(
            bounds,
            expectedAspectRatio: 16d / 9d,
            lockAspectRatio: false);

        Assert.Equal(bounds, result);
    }

    [Theory]
    [InlineData(double.NaN, 360d)]
    [InlineData(640d, 0d)]
    [InlineData(-1d, 360d)]
    public void Resize_RejectsInvalidCurrentDimensions(double width, double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TargetRegionGeometry.Resize(width, height, 0d, 0d, 16d / 9d, false));
    }
}
