using RemoteAnnotate.Client.Services;
using RemoteAnnotate.Contracts.Coordinates;

namespace RemoteAnnotate.Client.Tests.Services;

public sealed class DisplayCoordinateMapperTests
{
    private readonly DisplayCoordinateMapper mapper = new();

    [Theory]
    [InlineData(96d, 1d, 96d)]
    [InlineData(125d, 1.25d, 100d)]
    [InlineData(300d, 1.5d, 200d)]
    [InlineData(-1_920d, 1.5d, -1_280d)]
    public void PhysicalPixelsToDips_UsesMonitorScale(
        double pixels,
        double scaleFactor,
        double expected)
    {
        Assert.Equal(expected, mapper.PhysicalPixelsToDips(pixels, scaleFactor), precision: 12);
    }

    [Theory]
    [InlineData(96d, 1d, 96d)]
    [InlineData(100d, 1.25d, 125d)]
    [InlineData(-1_280d, 1.5d, -1_920d)]
    public void DipsToPhysicalPixels_UsesMonitorScale(
        double dips,
        double scaleFactor,
        double expected)
    {
        Assert.Equal(expected, mapper.DipsToPhysicalPixels(dips, scaleFactor), precision: 12);
    }

    [Theory]
    [InlineData(0d, 0d, 0d, 0d)]
    [InlineData(1d, 0d, 2_560d, 0d)]
    [InlineData(0.5d, 0.5d, 1_280d, 720d)]
    [InlineData(1d, 1d, 2_560d, 1_440d)]
    public void ToOverlayPoint_MapsNormalizedCoordinates(
        double normalizedX,
        double normalizedY,
        double expectedX,
        double expectedY)
    {
        var result = mapper.ToOverlayPoint(
            new NormalizedPoint(normalizedX, normalizedY),
            2_560d,
            1_440d);

        Assert.Equal(new PointD(expectedX, expectedY), result);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    public void Conversions_RejectInvalidScale(double scaleFactor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => mapper.PhysicalPixelsToDips(100d, scaleFactor));
    }
}
