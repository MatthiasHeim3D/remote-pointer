using RemotePointer.Client.Services;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Tests.Fakes;

internal sealed class FakeRelayClient : IRelayClient
{
    public event EventHandler<RelayConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

    public event EventHandler<AnnotatorJoinRequestedEventArgs>? AnnotatorJoinRequested;

    public event EventHandler<AnnotatorJoinCancelledEventArgs>? AnnotatorJoinCancelled;

    public event EventHandler<RelaySessionStateEventArgs>? SessionApproved;

    public event EventHandler<RelayHostDisplayChangedEventArgs>? HostDisplayChanged;

    public event EventHandler<RelayPointerEventArgs>? PointerReceived;

    public event EventHandler<RelayAcknowledgementEventArgs>? PointerDisplayed;

    public event EventHandler<RelaySessionEndedEventArgs>? SessionEnded;

    public event EventHandler? HostDirectoryChanged;

    public string ServerUrl { get; init; } = "https://relay.example";

    public RelayConnectionStatus Status { get; private set; } = RelayConnectionStatus.Connected;

    public string? SessionId { get; set; }

    public SessionCredential? Credential { get; set; }

    public CreateSessionResponse? CreateResponse { get; set; }

    public Exception? CreateException { get; set; }

    public JoinResponse JoinResponse { get; set; } = new(true, "session-1", null);

    public RelayCapabilities Capabilities { get; set; } =
        new(ServerPasswordRequired: false);

    public IReadOnlyList<AvailableHostDescriptor> AvailableHosts { get; set; } = [];

    public bool IsDiscoverable { get; private set; }

    public int DiscoverabilityUpdateCount { get; private set; }

    public Exception? DiscoverabilityException { get; set; }

    public DisplayDescriptor? UpdatedHostDisplay { get; private set; }

    public string? RequestedHostSessionId { get; private set; }

    public int CreateCount { get; private set; }

    public AnnotatorDescriptor? ApprovedAnnotator { get; private set; }

    public AnnotatorDescriptor? RejectedAnnotator { get; private set; }

    public int DisconnectAllConnectionsCount { get; private set; }

    public PointerEventMessage? SentPointer { get; private set; }

    public PointerAcknowledgement? SentAcknowledgement { get; private set; }

    public int EndCount { get; private set; }

    public Exception? EndException { get; set; }

    public bool Disposed { get; private set; }

    public bool ResumeResult { get; set; }

    public int ResumeCount { get; private set; }

    public Task<RelayCapabilities> GetRelayCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(Capabilities);
    }

    public string? ServerPasswordKey { get; private set; }

    public int ServerPasswordKeyUpdateCount { get; private set; }

    public Task SetServerPasswordKeyAsync(
        string? key,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ServerPasswordKey = key;
        ServerPasswordKeyUpdateCount++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AvailableHostDescriptor>> GetAvailableHostsAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(AvailableHosts);
    }

    public Task<bool> TryResumeSessionAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ResumeCount++;
        return Task.FromResult(ResumeResult);
    }

    public Task<CreateSessionResponse> CreateHostSessionAsync(
        DisplayDescriptor display,
        CancellationToken cancellationToken = default)
    {
        _ = display;
        _ = cancellationToken;
        CreateCount++;
        if (CreateException is not null)
        {
            return Task.FromException<CreateSessionResponse>(CreateException);
        }

        var response = CreateResponse ?? throw new InvalidOperationException("No create response configured.");
        SessionId = response.SessionId;
        Credential = response.Credential;
        IsDiscoverable = true;
        if (Status != RelayConnectionStatus.Connected)
        {
            RaiseConnectionStatus(RelayConnectionStatus.Connected, "Connected to relay.");
        }

        return Task.FromResult(response);
    }

    public Task<bool> SetHostDiscoverableAsync(
        bool discoverable,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        DiscoverabilityUpdateCount++;
        if (DiscoverabilityException is not null)
        {
            return Task.FromException<bool>(DiscoverabilityException);
        }

        IsDiscoverable = discoverable;
        return Task.FromResult(discoverable);
    }

    public Task<JoinResponse> RequestToJoinHostAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        RequestedHostSessionId = sessionId;
        _ = cancellationToken;
        SessionId = JoinResponse.SessionId;
        return Task.FromResult(JoinResponse);
    }

    public Task UpdateHostDisplayAsync(
        DisplayDescriptor display,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        UpdatedHostDisplay = display;
        return Task.CompletedTask;
    }

    public Task ApplyClientSettingsAsync(
        string displayName,
        string? profilePicturePath,
        int maximumAnnotatorConnections,
        CancellationToken cancellationToken = default)
    {
        _ = displayName;
        _ = profilePicturePath;
        _ = maximumAnnotatorConnections;
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public Task ApproveAnnotatorAsync(
        string sessionId,
        string annotatorConnectionId,
        CancellationToken cancellationToken = default)
    {
        _ = sessionId;
        _ = cancellationToken;
        ApprovedAnnotator = new AnnotatorDescriptor(
            annotatorConnectionId,
            "annotator-id",
            "Annotator",
            "1.0.0");
        return Task.CompletedTask;
    }

    public Task RejectAnnotatorAsync(
        string sessionId,
        string annotatorConnectionId,
        CancellationToken cancellationToken = default)
    {
        _ = sessionId;
        _ = cancellationToken;
        RejectedAnnotator = new AnnotatorDescriptor(
            annotatorConnectionId,
            "annotator-id",
            "Annotator",
            "1.0.0");
        return Task.CompletedTask;
    }

    public Task DisconnectAllConnectionsAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        DisconnectAllConnectionsCount++;
        return Task.CompletedTask;
    }

    public Task<bool> SendPointerAsync(
        PointerEventMessage pointerEvent,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        SentPointer = pointerEvent;
        return Task.FromResult(Status == RelayConnectionStatus.Connected);
    }

    public Task<bool> AcknowledgePointerAsync(
        PointerAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        SentAcknowledgement = acknowledgement;
        return Task.FromResult(Status == RelayConnectionStatus.Connected);
    }

    public Task EndSessionAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        EndCount++;
        if (EndException is not null)
        {
            return Task.FromException(EndException);
        }

        SessionId = null;
        Credential = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public void RaiseConnectionStatus(RelayConnectionStatus status, string message)
    {
        Status = status;
        ConnectionStatusChanged?.Invoke(
            this,
            new RelayConnectionStatusChangedEventArgs(status, message));
    }

    public void RaiseHostDirectoryChanged() =>
        HostDirectoryChanged?.Invoke(this, EventArgs.Empty);

    public void RaiseJoinRequest(AnnotatorDescriptor annotator) =>
        AnnotatorJoinRequested?.Invoke(this, new AnnotatorJoinRequestedEventArgs(annotator));

    public void RaiseJoinRequestCancelled(string annotatorConnectionId) =>
        AnnotatorJoinCancelled?.Invoke(
            this,
            new AnnotatorJoinCancelledEventArgs(annotatorConnectionId));

    public void RaiseApproved(SessionStateMessage state)
    {
        SessionId = state.SessionId;
        Credential ??= new SessionCredential(
            state.SessionId,
            ClientRole.Annotator,
            "annotator-id",
            new string('s', 32),
            new string('r', 32),
            state.ExpiresAt);
        SessionApproved?.Invoke(this, new RelaySessionStateEventArgs(state));
    }

    public void RaiseHostDisplayChanged(DisplayDescriptor display) =>
        HostDisplayChanged?.Invoke(
            this,
            new RelayHostDisplayChangedEventArgs(display));

    public void RaisePointer(PointerEventMessage pointerEvent) =>
        PointerReceived?.Invoke(this, new RelayPointerEventArgs(pointerEvent));

    public void RaiseAcknowledgement(PointerAcknowledgement acknowledgement) =>
        PointerDisplayed?.Invoke(this, new RelayAcknowledgementEventArgs(acknowledgement));

    public void RaiseSessionEnded(string reason, bool expired = false)
    {
        SessionId = null;
        Credential = null;
        SessionEnded?.Invoke(this, new RelaySessionEndedEventArgs(reason, expired));
    }
}
