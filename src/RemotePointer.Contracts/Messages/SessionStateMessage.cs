namespace RemotePointer.Contracts.Messages;

public sealed record SessionStateMessage(
    string SessionId,
    bool Approved,
    DisplayDescriptor? ReceiverDisplay,
    DateTimeOffset ExpiresAt,
    bool ReceiverDiscoverable = false,
    ConnectedPresenterDescriptor[]? ConnectedPresenters = null);
