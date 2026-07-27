using RemoteAnnotate.Contracts.Messages;

namespace RemoteAnnotate.Server.Sessions;

public interface ISessionManager
{
    int ActiveSessionCount { get; }

    RelayRoomChange SetConnectionRoom(string connectionId, string? room);

    string GetConnectionRoom(string connectionId);

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

    /// <summary>
    /// Records the colour an approved annotator would like to draw in and reallocates the
    /// session, returning every annotator whose effective colour moved as a result.
    /// </summary>
    IReadOnlyList<AnnotationColorAssignment> SetAnnotationColorPreference(
        string connectionId,
        string? preferredColor);

    /// <summary>
    /// Reallocates colours for a session after its membership changed, returning only the
    /// annotators whose colour moved. Safe to call for a session that is gone, which is what lets
    /// callers run it unconditionally after any departure.
    /// </summary>
    IReadOnlyList<AnnotationColorAssignment> RefreshAnnotationColors(string sessionId);

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
