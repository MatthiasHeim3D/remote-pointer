namespace RemotePointer.Server.Sessions;

public sealed class SessionOptions
{
    public const string SectionName = "Sessions";

    /// <summary>
    /// How long a freshly created session may sit without a single access request before the
    /// relay may collect it. It is only a grace period: a session that anything can still reach
    /// survives it, and one that is used is never collected early.
    /// </summary>
    public int AbandonedSessionLifetimeMinutes { get; set; } = 10;

    public int MaximumSessionHours { get; set; } = 8;

    public int SequenceWindowSize { get; set; } = 64;

    public int MaximumAnnotatorsPerHost { get; set; } = 16;

    /// <summary>
    /// Requires every client to present a server password before it can publish itself, list
    /// other clients, or request access. Turning this off puts clients that set no password
    /// into one open pool where they share names and pictures with anyone who reaches the relay.
    /// </summary>
    public bool RequireServerPassword { get; set; } = true;
}
