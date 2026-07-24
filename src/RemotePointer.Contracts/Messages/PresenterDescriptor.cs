namespace RemotePointer.Contracts.Messages;

public sealed record PresenterDescriptor(
    string ConnectionId,
    string ClientInstanceId,
    string DisplayName,
    string ClientVersion,
    byte[]? ProfilePicturePng = null);
