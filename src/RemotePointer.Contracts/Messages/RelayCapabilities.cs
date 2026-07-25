namespace RemotePointer.Contracts.Messages;

public sealed record RelayCapabilities(
    bool ReceiverDiscoveryEnabled,
    bool ServerPasswordRequired);
