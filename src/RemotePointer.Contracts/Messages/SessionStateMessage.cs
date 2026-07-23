namespace RemotePointer.Contracts.Messages;

public sealed record SessionStateMessage(
    string SessionId,
    bool Approved,
    DisplayDescriptor? ReceiverDisplay,
    DateTimeOffset ExpiresAt);
