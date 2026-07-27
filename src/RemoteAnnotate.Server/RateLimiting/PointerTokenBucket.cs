namespace RemoteAnnotate.Server.RateLimiting;

internal sealed class PointerTokenBucket(
    int eventsPerSecond,
    int burstSize,
    DateTimeOffset createdAt)
{
    private double availableTokens = burstSize;
    private DateTimeOffset lastRefill = createdAt;

    internal bool TryAcquire(DateTimeOffset now)
    {
        var elapsedSeconds = Math.Max(0d, (now - lastRefill).TotalSeconds);
        availableTokens = Math.Min(
            burstSize,
            availableTokens + (elapsedSeconds * eventsPerSecond));
        lastRefill = now;

        if (availableTokens < 1d)
        {
            return false;
        }

        availableTokens -= 1d;
        return true;
    }
}
