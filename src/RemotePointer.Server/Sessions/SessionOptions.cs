namespace RemotePointer.Server.Sessions;

public sealed class SessionOptions
{
    public const string SectionName = "Sessions";

    public int PairingCodeLifetimeMinutes { get; set; } = 10;

    public int MaximumSessionHours { get; set; } = 8;

    public int SequenceWindowSize { get; set; } = 64;

    public int MaximumPresentersPerReceiver { get; set; } = 16;

    public bool ReceiverDiscoveryEnabled { get; set; }

    /// <summary>
    /// Requires every client to present a server password before it can publish itself, list
    /// other clients, or request access. Turning this off puts clients that set no password
    /// into one open pool where they share names and pictures with anyone who reaches the relay.
    /// </summary>
    public bool RequireServerPassword { get; set; } = true;
}
