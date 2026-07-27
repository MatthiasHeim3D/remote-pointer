namespace RemoteAnnotate.Contracts.Messages;

public sealed record SessionStateMessage(
    string SessionId,
    bool Approved,
    DisplayDescriptor? HostDisplay,
    DateTimeOffset ExpiresAt,
    bool HostDiscoverable = false,
    ConnectedAnnotatorDescriptor[]? ConnectedAnnotators = null,
    string? HostClientInstanceId = null,
    byte[]? HostProfilePicturePng = null,
    string? HostDisplayName = null);
