namespace RemotePointer.Contracts.Messages;

public sealed record AnnotatorDescriptor(
    string ConnectionId,
    string ClientInstanceId,
    string DisplayName,
    string ClientVersion,
    byte[]? ProfilePicturePng = null);
