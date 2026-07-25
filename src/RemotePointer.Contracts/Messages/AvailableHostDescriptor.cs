namespace RemotePointer.Contracts.Messages;

public sealed record AvailableHostDescriptor(
    string SessionId,
    string DisplayName,
    string ApplicationInstanceId = "",
    byte[]? ProfilePicturePng = null);
