namespace RemotePointer.Contracts.Messages;

public sealed record AvailableReceiverDescriptor(
    string SessionId,
    string DisplayName,
    string ApplicationInstanceId = "",
    byte[]? ProfilePicturePng = null);
