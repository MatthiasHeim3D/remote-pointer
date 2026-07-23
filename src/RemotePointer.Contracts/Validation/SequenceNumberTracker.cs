namespace RemotePointer.Contracts.Validation;

public sealed class SequenceNumberTracker
{
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
