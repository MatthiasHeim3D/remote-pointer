using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Services;

public interface IRelayClient : IAsyncDisposable
{
    event EventHandler<RelayConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

    event EventHandler<PresenterJoinRequestedEventArgs>? PresenterJoinRequested;

    event EventHandler<PresenterJoinCancelledEventArgs>? PresenterJoinCancelled;

    event EventHandler<RelaySessionStateEventArgs>? SessionApproved;

    event EventHandler<RelayReceiverDisplayChangedEventArgs>? ReceiverDisplayChanged;

    event EventHandler<RelayPointerEventArgs>? PointerReceived;

    event EventHandler<RelayAcknowledgementEventArgs>? PointerDisplayed;

    event EventHandler<RelaySessionEndedEventArgs>? SessionEnded;

    event EventHandler? ReceiverDirectoryChanged;

    string ServerUrl { get; }

    RelayConnectionStatus Status { get; }

    string? SessionId { get; }

    SessionCredential? Credential { get; }

    Task<RelayCapabilities> GetRelayCapabilitiesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the derived group key to present on this and every later connection, and presents
    /// it right away when the connection is live. The relay keeps a connection in the group its
    /// last key derived to, so a key that is only stored locally leaves this client listed to,
    /// and joinable from, the password it just left.
    /// </summary>
    Task SetServerPasswordKeyAsync(string? key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AvailableReceiverDescriptor>> GetAvailableReceiversAsync(
        CancellationToken cancellationToken = default);

    Task<bool> TryResumeSessionAsync(CancellationToken cancellationToken = default);

    Task<CreateSessionResponse> CreateReceiverSessionAsync(
        DisplayDescriptor display,
        CancellationToken cancellationToken = default);

    Task<bool> SetReceiverDiscoverableAsync(
        bool discoverable,
        CancellationToken cancellationToken = default);

    Task<JoinResponse> RequestToJoinReceiverAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task UpdateReceiverDisplayAsync(
        DisplayDescriptor display,
        CancellationToken cancellationToken = default);

    Task ApplyClientSettingsAsync(
        string displayName,
        string? profilePicturePath,
        int maximumPresenterConnections,
        CancellationToken cancellationToken = default);

    Task ApprovePresenterAsync(
        string sessionId,
        string presenterConnectionId,
        CancellationToken cancellationToken = default);

    Task RejectPresenterAsync(
        string sessionId,
        string presenterConnectionId,
        CancellationToken cancellationToken = default);

    Task DisconnectAllConnectionsAsync(CancellationToken cancellationToken = default);

    Task<bool> SendPointerAsync(
        PointerEventMessage pointerEvent,
        CancellationToken cancellationToken = default);

    Task<bool> AcknowledgePointerAsync(
        PointerAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default);

    Task EndSessionAsync(CancellationToken cancellationToken = default);
}
