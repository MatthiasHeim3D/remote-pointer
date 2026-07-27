using System.Windows;
using RemoteAnnotate.Client.Overlays;

namespace RemoteAnnotate.Client.Tests.Overlays;

public sealed class FreehandPathBuilderTests
{
    private static readonly Point Start = new(0d, 0d);

    [Fact]
    public void Append_WithoutSmoothingKeepsEverySampleExactly()
    {
        var points = NewLine();
        var builder = new FreehandPathBuilder(Start, smooth: false);

        builder.Append(new Point(10d, 6d), points);
        builder.Append(new Point(20d, 0d), points);

        Assert.Equal(3, points.Count);
        Assert.Equal(new Point(10d, 6d), points[1]);
        Assert.Equal(new Point(20d, 0d), points[2]);
    }

    [Fact]
    public void Append_SmoothsASampleOnlyOnceItsSuccessorHasArrived()
    {
        var points = NewLine();
        var builder = new FreehandPathBuilder(Start, smooth: true);

        builder.Append(new Point(10d, 4d), points);

        // Nothing follows it yet, so it is drawn exactly where it arrived.
        Assert.Equal(2, points.Count);
        Assert.Equal(new Point(10d, 4d), points[1]);

        builder.Append(new Point(20d, 0d), points);

        Assert.Equal(3, points.Count);
        Assert.Equal(new Point(10d, 2d), points[1]);
        Assert.Equal(new Point(20d, 0d), points[2]);
    }

    [Fact]
    public void Append_LeavesTheFirstAndNewestSamplesWhereTheyArrived()
    {
        var release = new Point(41d, 17d);
        var points = NewLine();
        var builder = new FreehandPathBuilder(Start, smooth: true);
        foreach (var sample in new[] { new Point(9d, 22d), new Point(23d, 3d), new Point(31d, 25d) })
        {
            builder.Append(sample, points);
        }

        builder.Append(release, points);

        // A released gesture has to end on the annotator's real release position.
        Assert.Equal(Start, points[0]);
        Assert.Equal(release, points[^1]);
    }

    [Fact]
    public void Append_SmoothsAgainstTheSamplesThatArrivedRatherThanTheSmoothedOnes()
    {
        var points = NewLine();
        var builder = new FreehandPathBuilder(Start, smooth: true);

        builder.Append(new Point(10d, 8d), points);
        builder.Append(new Point(20d, 0d), points);
        builder.Append(new Point(30d, 8d), points);

        // The middle sample is already smoothed to (10, 4) in the line, but the one after it is
        // weighted against the (10, 8) that was actually received, so a sample is never smoothed
        // twice and the line cannot creep away from what was drawn.
        Assert.Equal(4d, points[1].Y, 12);
        Assert.Equal(4d, points[2].Y, 12);
    }

    [Fact]
    public void Append_IgnoresARepeatedSample()
    {
        var points = NewLine();
        var builder = new FreehandPathBuilder(Start, smooth: true);

        builder.Append(new Point(10d, 10d), points);
        builder.Append(new Point(10d, 10d), points);

        Assert.Equal(2, points.Count);
        Assert.Equal(new Point(10d, 10d), points[1]);
    }

    [Fact]
    public void Append_ThinsTheLineWhenItReachesItsCap()
    {
        var release = new Point(9_000d, 9_000d);
        var points = NewLine();
        var builder = new FreehandPathBuilder(Start, smooth: true);
        for (var index = 1; index < FreehandPathBuilder.MaximumPoints; index++)
        {
            builder.Append(new Point(index, index), points);
        }

        Assert.Equal(FreehandPathBuilder.MaximumPoints, points.Count);

        builder.Append(release, points);

        Assert.InRange(points.Count, 2, FreehandPathBuilder.MaximumPoints - 1);
        Assert.Equal(Start, points[0]);
        Assert.Equal(release, points[^1]);
        // Only interior detail is dropped, so the line still runs from one end to the other.
        for (var index = 1; index < points.Count; index++)
        {
            Assert.True(points[index].X > points[index - 1].X);
        }
    }

    private static List<Point> NewLine() => [Start];
}
