namespace RemotePointer.Contracts.Messages;

public sealed record AvailableReceiverDescriptor(
    string SessionId,
    string DisplayName);
