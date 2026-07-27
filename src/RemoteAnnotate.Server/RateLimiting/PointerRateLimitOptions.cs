namespace RemoteAnnotate.Server.RateLimiting;

public sealed class PointerRateLimitOptions
{
    public const string SectionName = "RateLimits";

    public int EventsPerSecond { get; set; } = 90;

    public int BurstSize { get; set; } = 180;
}
