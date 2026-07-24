namespace RemotePointer.Contracts.Messages;

public sealed record ConnectedPresenterDescriptor(
    string DisplayName,
    byte[]? ProfilePicturePng = null);
