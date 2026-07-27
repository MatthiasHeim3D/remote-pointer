using RemoteAnnotate.Contracts.Validation;

namespace RemoteAnnotate.Contracts.Tests.Validation;

public sealed class SequenceNumberTrackerTests
{
    [Fact]
    public void TryAccept_AcceptsIncreasingSequence()
    {
        var tracker = new SequenceNumberTracker();

        Assert.True(tracker.TryAccept(0));
        Assert.True(tracker.TryAccept(1));
        Assert.True(tracker.TryAccept(2));
    }

    [Fact]
    public void TryAccept_RejectsDuplicate()
    {
        var tracker = new SequenceNumberTracker();

        Assert.True(tracker.TryAccept(12));
        Assert.False(tracker.TryAccept(12));
    }

    [Fact]
    public void TryAccept_AcceptsUniqueNumberWithinReorderingWindow()
    {
        var tracker = new SequenceNumberTracker(windowSize: 4);

        Assert.True(tracker.TryAccept(10));
        Assert.True(tracker.TryAccept(8));
    }

    [Fact]
    public void TryAccept_RejectsSignificantlyOutOfOrderNumber()
    {
        var tracker = new SequenceNumberTracker(windowSize: 4);

        Assert.True(tracker.TryAccept(10));
        Assert.False(tracker.TryAccept(6));
    }

    [Fact]
    public void TryAccept_RejectsNegativeNumber()
    {
        Assert.False(new SequenceNumberTracker().TryAccept(-1));
    }

    [Fact]
    public void TryAccept_RejectsAnImplausibleForwardJump()
    {
        var tracker = new SequenceNumberTracker();

        Assert.True(tracker.TryAccept(1_000));
        Assert.False(tracker.TryAccept(long.MaxValue));
        Assert.False(
            tracker.TryAccept(1_000 + SequenceNumberTracker.MaximumForwardGap + 1));
        Assert.True(tracker.TryAccept(1_001));
    }

    [Fact]
    public void TryAccept_AcceptsAnyStartingPointAndNormalProgress()
    {
        var tracker = new SequenceNumberTracker();
        var sessionStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_024L;

        Assert.True(tracker.TryAccept(sessionStart));
        Assert.True(tracker.TryAccept(sessionStart + 1));
        Assert.True(tracker.TryAccept(sessionStart + SequenceNumberTracker.MaximumForwardGap));
    }
}
