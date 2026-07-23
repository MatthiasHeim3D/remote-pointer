namespace RemotePointer.Server.RateLimiting;

public sealed class PointerRateLimitOptions
{
    public const string SectionName = "RateLimits";

    public int EventsPerSecond { get; set; } = 20;

    public int BurstSize { get; set; } = 30;
}
