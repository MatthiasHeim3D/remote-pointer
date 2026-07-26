using RemotePointer.Contracts.Messages;

namespace RemotePointer.Server.Sessions;

public interface ISessionManager
{
    int ActiveSessionCount { get; }

    bool ServerPasswordRequired { get; }

    RelayGroupChange SetConnectionGroup(string connectionId, string? groupKey);

    string GetConnectionGroup(string connectionId);

    CreateSessionResponse CreateHostSession(
        DisplayDescriptor display,
        string connectionId,
        string clientInstanceId,
        string hostDisplayName,
        string? applicationInstanceId = null,
        ClientProfile? profile = null,
        int? maximumAnnotatorConnections = null);

    IReadOnlyList<AvailableHostDescriptor> GetAvailableHosts(
        string? excludedApplicationInstanceId = null,
        string? connectionId = null);

    bool SetHostDiscoverable(
        string sessionId,
        string hostConnectionId,
        bool discoverable);

    JoinSessionResult RequestToJoinHost(
        DirectJoinRequest request,
        string connectionId,
        string displayName,
        string? applicationInstanceId = null);

    HostDisplayUpdateResult UpdateHostDisplay(
        string sessionId,
        string hostConnectionId,
        DisplayDescriptor display);

    HostClientSettingsUpdateResult UpdateHostClientSettings(
        string sessionId,
        string hostConnectionId,
        string hostDisplayName,
        ClientProfile profile,
        int maximumAnnotatorConnections);

    ApproveAnnotatorResult ApproveAnnotator(
        string sessionId,
        string annotatorConnectionId,
        string hostConnectionId);

    RejectAnnotatorResult RejectAnnotator(
        string sessionId,
        string annotatorConnectionId,
        string hostConnectionId);

    PointerRelayResult AcceptPointer(string connectionId, PointerEventMessage pointerEvent);

    AcknowledgementRelayResult AcceptAcknowledgement(
        string connectionId,
        PointerAcknowledgement acknowledgement);

    ResumeSessionResult ResumeSession(
        string connectionId,
        SessionResumeRequest request,
        string? applicationInstanceId = null);

    SessionTerminationResult DisconnectAnnotators(
        string sessionId,
        string hostConnectionId);

    SessionTerminationResult DisconnectAnnotator(
        string sessionId,
        string hostConnectionId,
        string annotatorId);

    /// <summary>
    /// Pauses or resumes one annotator, or every connected annotator when
    /// <paramref name="annotatorId"/> is null.
    /// </summary>
    AnnotatorPauseResult SetAnnotatorPaused(
        string sessionId,
        string hostConnectionId,
        string? annotatorId,
        bool paused);

    SessionTerminationResult EndSession(string sessionId, string connectionId);

    IReadOnlyList<SessionTerminationResult> CollectExpiredSessions();

    ConnectionDisconnectResult? Disconnect(string connectionId);
}
