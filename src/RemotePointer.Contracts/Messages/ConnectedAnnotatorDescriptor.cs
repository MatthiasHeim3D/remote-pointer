namespace RemotePointer.Contracts.Messages;

public sealed record ConnectedAnnotatorDescriptor(
    string DisplayName,
    byte[]? ProfilePicturePng = null);
