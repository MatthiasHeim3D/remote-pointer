using System.Windows;
using RemoteAnnotate.Client.Overlays;

namespace RemoteAnnotate.Client.Tests.Overlays;

public sealed class GestureMotionTests
{
    [Fact]
    public void Advance_CoversPartOfTheDistanceWithoutOvershooting()
    {
        var advanced = GestureMotion.Advance(
            new Point(0d, 0d),
            new Point(100d, 200d),
            elapsedMilliseconds: 16d,
            timeConstantMilliseconds: 35d);

        Assert.InRange(advanced.X, 0d, 100d);
        Assert.InRange(advanced.Y, 0d, 200d);
        // Both axes travel the same fraction, so the shape keeps its direction while it catches up.
        Assert.Equal(advanced.X / 100d, advanced.Y / 200d, 12);
    }

    [Fact]
    public void Advance_CoversTheSameGroundWhateverTheFrameRate()
    {
        var target = new Point(120d, 0d);
        var single = GestureMotion.Advance(
            new Point(0d, 0d),
            target,
            elapsedMilliseconds: 32d,
            timeConstantMilliseconds: 35d);

        var halved = GestureMotion.Advance(
            new Point(0d, 0d),
            target,
            elapsedMilliseconds: 16d,
            timeConstantMilliseconds: 35d);
        halved = GestureMotion.Advance(
            halved,
            target,
            elapsedMilliseconds: 16d,
            timeConstantMilliseconds: 35d);

        Assert.Equal(single.X, halved.X, 9);
    }

    [Fact]
    public void Advance_StaysPutWhenNoTimeHasPassed()
    {
        var current = new Point(12d, 34d);

        var advanced = GestureMotion.Advance(
            current,
            new Point(500d, 500d),
            elapsedMilliseconds: 0d,
            timeConstantMilliseconds: 35d);

        Assert.Equal(current, advanced);
    }

    [Fact]
    public void Advance_ArrivesImmediatelyWithoutATimeConstant()
    {
        var target = new Point(500d, 500d);

        var advanced = GestureMotion.Advance(
            new Point(12d, 34d),
            target,
            elapsedMilliseconds: 16d,
            timeConstantMilliseconds: 0d);

        Assert.Equal(target, advanced);
    }

    [Fact]
    public void Advance_ApproachesTheTargetCloselyWithinTheSettleDeadline()
    {
        // A released drag stops being animated at the settle deadline, so the point it has
        // reached by then has to be indistinguishable from its final one at any drag speed.
        var target = new Point(400d, 0d);
        var current = new Point(0d, 0d);
        for (var elapsed = 0L; elapsed < GestureMotion.MaximumSettleMilliseconds; elapsed += 16L)
        {
            current = GestureMotion.Advance(
                current,
                target,
                elapsedMilliseconds: 16d,
                GestureMotion.MotionTimeConstantMilliseconds);
        }

        Assert.True(Math.Abs(target.X - current.X) < 1d);
    }

    [Theory]
    [InlineData(0d, 0d, true)]
    [InlineData(0.05d, -0.05d, true)]
    [InlineData(0.5d, 0d, false)]
    [InlineData(0d, 0.5d, false)]
    public void HasArrived_TreatsOnlySubPixelDifferencesAsArrived(
        double offsetX,
        double offsetY,
        bool expected)
    {
        var arrived = GestureMotion.HasArrived(
            new Point(10d, 10d),
            new Point(10d + offsetX, 10d + offsetY));

        Assert.Equal(expected, arrived);
    }

    [Fact]
    public void PathPointsToRelease_ReleasesNothingWhenNothingIsQueued()
    {
        var released = GestureMotion.PathPointsToRelease(
            queuedCount: 0,
            elapsedMilliseconds: 16d,
            timeConstantMilliseconds: 30d);

        Assert.Equal(0, released);
    }

    [Fact]
    public void PathPointsToRelease_AlwaysReleasesAtLeastOnePointSoALineCannotStall()
    {
        var released = GestureMotion.PathPointsToRelease(
            queuedCount: 1,
            elapsedMilliseconds: 0.1d,
            timeConstantMilliseconds: 30d);

        Assert.Equal(1, released);
    }

    [Fact]
    public void PathPointsToRelease_SpreadsABurstOverSeveralFrames()
    {
        var queued = 12;
        var frames = 0;
        while (queued > 0)
        {
            var released = GestureMotion.PathPointsToRelease(
                queued,
                elapsedMilliseconds: 16d,
                timeConstantMilliseconds: 30d);
            Assert.InRange(released, 1, queued);
            queued -= released;
            frames++;
        }

        Assert.True(frames > 1);
    }

    [Fact]
    public void PathPointsToRelease_DrainsEverythingOnceAFullTimeConstantHasPassed()
    {
        var released = GestureMotion.PathPointsToRelease(
            queuedCount: 12,
            elapsedMilliseconds: 30d,
            timeConstantMilliseconds: 30d);

        Assert.Equal(12, released);
    }

    [Fact]
    public void PathPointsToRelease_HoldsTheQueueWhenNoTimeHasPassed()
    {
        var released = GestureMotion.PathPointsToRelease(
            queuedCount: 4,
            elapsedMilliseconds: 0d,
            timeConstantMilliseconds: 30d);

        Assert.Equal(0, released);
    }

    [Fact]
    public void Smooth_LeavesAPointThatIsAlreadyInLineAlone()
    {
        var smoothed = GestureMotion.Smooth(
            new Point(0d, 0d),
            new Point(10d, 10d),
            new Point(20d, 20d));

        Assert.Equal(10d, smoothed.X, 12);
        Assert.Equal(10d, smoothed.Y, 12);
    }

    [Fact]
    public void Smooth_HalvesHowFarASampleStrayedFromItsNeighbours()
    {
        var smoothed = GestureMotion.Smooth(
            new Point(0d, 0d),
            new Point(10d, 4d),
            new Point(20d, 0d));

        // Along the line the sample stays where it was; across it, only half the excursion is left.
        Assert.Equal(10d, smoothed.X, 12);
        Assert.Equal(2d, smoothed.Y, 12);
    }

    [Fact]
    public void Smooth_RoundsASharpCornerByOnlyAFractionOfOneSampleStep()
    {
        const double sampleStep = 20d;
        var corner = new Point(100d, 100d);

        var smoothed = GestureMotion.Smooth(
            new Point(100d - sampleStep, 100d),
            corner,
            new Point(100d, 100d - sampleStep));

        // A right angle is the worst case for a three-tap pass, and even there the corner only
        // moves about a third of the distance between two samples — a pixel or two at the rate
        // a freehand line is really sampled at, rather than a shape rounded off.
        var displacement = Math.Sqrt(
            Math.Pow(smoothed.X - corner.X, 2d) + Math.Pow(smoothed.Y - corner.Y, 2d));
        Assert.InRange(displacement / sampleStep, 0.3d, 0.4d);
    }
}
