using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Services;

public interface IRelayClient : IAsyncDisposable
{
    event EventHandler<RelayConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

    event EventHandler<AnnotatorJoinRequestedEventArgs>? AnnotatorJoinRequested;

    event EventHandler<AnnotatorJoinCancelledEventArgs>? AnnotatorJoinCancelled;

    event EventHandler<RelaySessionStateEventArgs>? SessionApproved;

    event EventHandler<RelayHostDisplayChangedEventArgs>? HostDisplayChanged;

    event EventHandler<RelayPointerEventArgs>? PointerReceived;

    event EventHandler<RelayAcknowledgementEventArgs>? PointerDisplayed;

    event EventHandler<RelaySessionEndedEventArgs>? SessionEnded;

    /// <summary>
    /// Raised on an annotator when the host pauses or resumes it. Its session stays up; only its
    /// pointer events stop being relayed.
    /// </summary>
    event EventHandler<RelayAnnotationPausedEventArgs>? AnnotationPausedChanged;

    /// <summary>
    /// Raised on an annotator when the relay allocates it a drawing colour. That is its own
    /// preference whenever it can have it, and a free preset when an annotator ahead of it
    /// already holds it; it is raised again with the preference once the clash clears.
    /// </summary>
    event EventHandler<RelayAnnotationColorEventArgs>? AnnotationColorAssigned;

    event EventHandler? HostDirectoryChanged;

    string ServerUrl { get; }

    RelayConnectionStatus Status { get; }

    string? SessionId { get; }

    SessionCredential? Credential { get; }

    Task<RelayCapabilities> GetRelayCapabilitiesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the derived key this client presents to get onto the relay. The key is presented
    /// when a connection is established, so a live connection — which the old password admitted
    /// — is dropped and the next call reconnects with the new one.
    /// </summary>
    Task SetServerPasswordKeyAsync(string? key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Names the room whose directory this client takes part in, and names it to the relay right
    /// away when the connection is live. The relay keeps a connection in the room it last named,
    /// so a room that is only stored locally leaves this client listed in, and joinable from,
    /// the room it just left.
    /// </summary>
    Task SetRoomAsync(string? name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AvailableHostDescriptor>> GetAvailableHostsAsync(
        CancellationToken cancellationToken = default);

    Task<bool> TryResumeSessionAsync(CancellationToken cancellationToken = default);

    Task<CreateSessionResponse> CreateHostSessionAsync(
        DisplayDescriptor display,
        CancellationToken cancellationToken = default);

    Task<bool> SetHostDiscoverableAsync(
        bool discoverable,
        CancellationToken cancellationToken = default);

    Task<JoinResponse> RequestToJoinHostAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task UpdateHostDisplayAsync(
        DisplayDescriptor display,
        CancellationToken cancellationToken = default);

    Task ApplyClientSettingsAsync(
        string displayName,
        string? profilePicturePath,
        int maximumAnnotatorConnections,
        CancellationToken cancellationToken = default);

    Task ApproveAnnotatorAsync(
        string sessionId,
        string annotatorConnectionId,
        CancellationToken cancellationToken = default);

    Task RejectAnnotatorAsync(
        string sessionId,
        string annotatorConnectionId,
        CancellationToken cancellationToken = default);

    Task DisconnectAllConnectionsAsync(CancellationToken cancellationToken = default);

    Task DisconnectAnnotatorAsync(
        string annotatorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses or resumes one annotator, or every connected one when <paramref name="annotatorId"/>
    /// is null.
    /// </summary>
    Task SetAnnotatorPausedAsync(
        string? annotatorId,
        bool paused,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the relay which colour this annotator would like. The relay answers through
    /// <see cref="AnnotationColorAssigned"/>, which may name a different one.
    /// </summary>
    Task SetAnnotationColorPreferenceAsync(
        string color,
        CancellationToken cancellationToken = default);

    Task<bool> SendPointerAsync(
        PointerEventMessage pointerEvent,
        CancellationToken cancellationToken = default);

    Task<bool> AcknowledgePointerAsync(
        PointerAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default);

    Task EndSessionAsync(CancellationToken cancellationToken = default);
}
