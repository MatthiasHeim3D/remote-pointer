namespace RemotePointer.Contracts.Validation;

public sealed class SequenceNumberTracker
{
    /// <summary>
    /// How far ahead of the highest accepted number a sender may jump. A sender increments by
    /// one per event within a session, so this only rejects implausible values - including one
    /// close to <see cref="long.MaxValue"/>, which would move the window past every number the
    /// sender could go on to use and stall its own stream until it is approved again.
    /// </summary>
    public const long MaximumForwardGap = 1L << 20;

    private readonly Lock syncRoot = new();
    private readonly HashSet<long> acceptedNumbers = [];
    private readonly int windowSize;
    private long highestAccepted = -1;

    public SequenceNumberTracker(int windowSize = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 1);
        this.windowSize = windowSize;
    }

    public bool TryAccept(long sequenceNumber)
    {
        if (sequenceNumber < 0)
        {
            return false;
        }

        lock (syncRoot)
        {
            if (highestAccepted >= 0 && sequenceNumber - highestAccepted > MaximumForwardGap)
            {
                return false;
            }

            var lowestPermitted = Math.Max(0, highestAccepted - windowSize + 1);
            if (sequenceNumber < lowestPermitted || !acceptedNumbers.Add(sequenceNumber))
            {
                return false;
            }

            if (sequenceNumber > highestAccepted)
            {
                highestAccepted = sequenceNumber;
                lowestPermitted = Math.Max(0, highestAccepted - windowSize + 1);
                acceptedNumbers.RemoveWhere(number => number < lowestPermitted);
            }

            return true;
        }
    }
}
