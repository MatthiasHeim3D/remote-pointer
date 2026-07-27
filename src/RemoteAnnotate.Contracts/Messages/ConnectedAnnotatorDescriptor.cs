namespace RemoteAnnotate.Contracts.Messages;

/// <summary>
/// One annotator the host currently holds a connection to. <paramref name="AnnotatorId"/> is the
/// annotator's client instance id: the host addresses pause and disconnect requests to it, and it
/// outlives the relay connection id, which a reconnecting annotator replaces.
/// </summary>
public sealed record ConnectedAnnotatorDescriptor(
    string DisplayName,
    byte[]? ProfilePicturePng = null,
    string AnnotatorId = "",
    bool IsPaused = false);
