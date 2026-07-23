using RemotePointer.Contracts.Validation;

namespace RemotePointer.Contracts.Tests.Validation;

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
}
