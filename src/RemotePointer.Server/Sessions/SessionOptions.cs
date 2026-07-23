namespace RemotePointer.Server.Sessions;

public sealed class SessionOptions
{
    public const string SectionName = "Sessions";

    public int PairingCodeLifetimeMinutes { get; set; } = 10;

    public int MaximumSessionHours { get; set; } = 8;

    public int SequenceWindowSize { get; set; } = 64;

    public bool ReceiverDiscoveryEnabled { get; set; }
}
