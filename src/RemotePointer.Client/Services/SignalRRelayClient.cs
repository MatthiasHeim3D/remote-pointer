using System.Reflection;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Security;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using RemotePointer.Client.Configuration;
using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Serialization;
using RemotePointer.Contracts.Validation;

namespace RemotePointer.Client.Services;

public sealed class SignalRRelayClient : IRelayClient
{
    private const int TransitionSettleMilliseconds = 5_000;
    private const int TransitionPollMilliseconds = 50;

    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly IClientAuditLog? auditLog;
    private readonly HubConnection connection;
    private readonly string clientInstanceId;
    private ClientProfile clientProfile;
    private int maximumAnnotatorConnections;
    private string displayName;
    private readonly ClientRole? expectedRole;
    private readonly IProtectedSessionStore? sessionStore;
    private readonly SynchronizationContext? synchronizationContext;
    private readonly object stateLock = new();
    private bool disposed;
    private SessionCredential? credential;
    private string? passwordKey;
    private string room;
    private string? enteredRoom;
    private string? sessionId;
    private RelayConnectionStatus status = RelayConnectionStatus.Disconnected;

    public SignalRRelayClient(
        ClientSettings settings,
        IClientInstanceIdProvider clientInstanceIdProvider,
        ClientRole? expectedRole = null,
        IProtectedSessionStore? sessionStore = null,
        IClientAuditLog? auditLog = null)
        : this(
            ValidateProductionSettings(settings),
            clientInstanceIdProvider,
            null,
            null,
            expectedRole,
            sessionStore,
            auditLog)
    {
    }

    internal SignalRRelayClient(
        ClientSettings settings,
        IClientInstanceIdProvider clientInstanceIdProvider,
        Func<HttpMessageHandler>? messageHandlerFactory = null,
        HttpTransportType? transport = null,
        ClientRole? expectedRole = null,
        IProtectedSessionStore? sessionStore = null,
        IClientAuditLog? auditLog = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clientInstanceIdProvider);

        ServerUrl = settings.Server.BaseUrl.TrimEnd('/');
        clientInstanceId = clientInstanceIdProvider.GetClientInstanceId();
        var applicationInstanceId = clientInstanceIdProvider.GetApplicationInstanceId();
        clientProfile = CreateClientProfile(settings.Profile.PicturePath);
        maximumAnnotatorConnections = settings.Host.MaximumAnnotatorConnections;
        this.expectedRole = expectedRole;
        this.sessionStore = sessionStore;
        this.auditLog = auditLog;
        if ((expectedRole is null) != (sessionStore is null))
        {
            throw new ArgumentException(
                "An expected role and protected session store must be supplied together.");
        }

        if (expectedRole is not null)
        {
            credential = sessionStore!.Load(expectedRole.Value, clientInstanceId);
            sessionId = credential?.SessionId;
        }

        passwordKey = string.IsNullOrWhiteSpace(settings.Server.PasswordKey)
            ? null
            : settings.Server.PasswordKey;
        room = RoomName.Normalize(settings.Server.Room);
        synchronizationContext = SynchronizationContext.Current;
        displayName = string.IsNullOrWhiteSpace(settings.Profile.UserName)
            ? Environment.MachineName
            : settings.Profile.UserName.Trim();
        var hubUrl = $"{ServerUrl}/hubs/pointer"
            + $"?clientInstanceId={Uri.EscapeDataString(clientInstanceId)}"
            + $"&applicationInstanceId={Uri.EscapeDataString(applicationInstanceId)}"
            + $"&displayName={Uri.EscapeDataString(displayName)}";
        var reconnectDelays = settings.Server.ReconnectDelaysSeconds
            .Select(delay => TimeSpan.FromSeconds(delay))
            .ToArray();

        var builder = new HubConnectionBuilder()
            .WithUrl(
                hubUrl,
                options =>
                {
                    // The relay demands the derived key before it accepts the connection at
                    // all. It is read fresh on every connect and reconnect, so a password
                    // changed in Settings is presented by the next attempt, and it travels in
                    // the Authorization header rather than the query string to keep it out of
                    // proxy access logs.
                    options.AccessTokenProvider = () =>
                    {
                        lock (stateLock)
                        {
                            return Task.FromResult(passwordKey);
                        }
                    };
                    if (messageHandlerFactory is not null)
                    {
                        options.HttpMessageHandlerFactory = _ => messageHandlerFactory();
                    }

                    if (transport is not null)
                    {
                        options.Transports = transport.Value;
                    }
                })
            .WithAutomaticReconnect(reconnectDelays)
            .AddJsonProtocol(
                options => RemotePointerJson.Configure(options.PayloadSerializerOptions));
        connection = builder.Build();
        RegisterCallbacks();
    }

    public event EventHandler<RelayConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

    public event EventHandler? HostDirectoryChanged;

    public event EventHandler<AnnotatorJoinRequestedEventArgs>? AnnotatorJoinRequested;

    public event EventHandler<AnnotatorJoinCancelledEventArgs>? AnnotatorJoinCancelled;

    public event EventHandler<RelaySessionStateEventArgs>? SessionApproved;

    public event EventHandler<RelayHostDisplayChangedEventArgs>? HostDisplayChanged;

    public event EventHandler<RelayPointerEventArgs>? PointerReceived;

    public event EventHandler<RelayAcknowledgementEventArgs>? PointerDisplayed;

    public event EventHandler<RelaySessionEndedEventArgs>? SessionEnded;

    public event EventHandler<RelayAnnotationPausedEventArgs>? AnnotationPausedChanged;

    public event EventHandler<RelayAnnotationColorEventArgs>? AnnotationColorAssigned;

    public string ServerUrl { get; }

    public RelayConnectionStatus Status
    {
        get
        {
            lock (stateLock)
            {
                return status;
            }
        }
    }

    public string? SessionId
    {
        get
        {
            lock (stateLock)
            {
                return sessionId;
            }
        }
    }

    public SessionCredential? Credential
    {
        get
        {
            lock (stateLock)
            {
                return credential;
            }
        }
    }

    public async Task<RelayCapabilities> GetRelayCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await connection.InvokeAsync<RelayCapabilities>(
                "GetRelayCapabilities",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetServerPasswordKeyAsync(
        string? key,
        CancellationToken cancellationToken = default)
    {
        lock (stateLock)
        {
            var normalized = string.IsNullOrWhiteSpace(key) ? null : key;
            if (string.Equals(passwordKey, normalized, StringComparison.Ordinal))
            {
                return;
            }

            passwordKey = normalized;
        }

        // The password is presented when the connection is established, so a live connection
        // was admitted by the old one and has to be replaced rather than told about the new
        // one. A connection that is not up yet simply presents the new key when it starts.
        if (disposed || connection.State == HubConnectionState.Disconnected)
        {
            return;
        }

        await connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connectionGate.Release();
        }
    }

    public async Task SetRoomAsync(string? name, CancellationToken cancellationToken = default)
    {
        lock (stateLock)
        {
            room = RoomName.Normalize(name);
        }

        // Unlike the password, the room is per connection state the relay holds, so a live
        // connection has to name the new one now: until it does, this client is still listed
        // in — and joinable from — the room it left.
        if (disposed || connection.State != HubConnectionState.Connected)
        {
            return;
        }

        await connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnterRoomAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<AvailableHostDescriptor>> GetAvailableHostsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await connection.InvokeAsync<AvailableHostDescriptor[]>(
                "GetAvailableHosts",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryResumeSessionAsync(CancellationToken cancellationToken = default)
    {
        var currentCredential = Credential;
        if (currentCredential is null)
        {
            return false;
        }

        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await ResumeSessionAsync(currentCredential, cancellationToken).ConfigureAwait(false);
            SetStatus(RelayConnectionStatus.Connected, "Recovered and resumed the previous session.");
            auditLog?.Write(
                ClientAuditEvent.SessionRestored,
                sessionId: currentCredential.SessionId,
                role: currentCredential.Role);
            return true;
        }
        catch (Exception exception)
        {
            HandleResumeFailure(exception);
            return false;
        }
    }

    public async Task<CreateSessionResponse> CreateHostSessionAsync(
        DisplayDescriptor display,
        CancellationToken cancellationToken = default)
    {
        EnsureValid(ContractValidator.Validate(display));
        DiscardRecoveredCredential(ClientRole.Host);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var response = await connection.InvokeAsync<CreateSessionResponse>(
                "CreateHostSession",
                display,
                clientProfile,
                maximumAnnotatorConnections,
                displayName,
                cancellationToken)
            .ConfigureAwait(false);
        SetSession(response.SessionId, response.Credential);
        return response;
    }

    public async Task<bool> SetHostDiscoverableAsync(
        bool discoverable,
        CancellationToken cancellationToken = default)
    {
        var currentSessionId = SessionId
            ?? throw new InvalidOperationException("This host is not available on the relay.");
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await connection.InvokeAsync<bool>(
                "SetHostDiscoverable",
                currentSessionId,
                discoverable,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<JoinResponse> RequestToJoinHostAsync(
        string selectedSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedSessionId);
        DiscardRecoveredCredential(ClientRole.Annotator);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var request = new DirectJoinRequest(
            selectedSessionId,
            clientInstanceId,
            GetClientVersion(),
            clientProfile);
        var response = await connection.InvokeAsync<JoinResponse>(
                "RequestToJoinHost",
                request,
                displayName,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.Accepted)
        {
            lock (stateLock)
            {
                sessionId = response.SessionId;
            }
        }

        return response;
    }

    public async Task UpdateHostDisplayAsync(
        DisplayDescriptor display,
        CancellationToken cancellationToken = default)
    {
        EnsureValid(ContractValidator.Validate(display));
        var currentSessionId = SessionId
            ?? throw new InvalidOperationException("This host is not available on the relay.");
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await connection.InvokeAsync(
                "UpdateHostDisplay",
                currentSessionId,
                display,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ApplyClientSettingsAsync(
        string newDisplayName,
        string? profilePicturePath,
        int newMaximumAnnotatorConnections,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newDisplayName);
        if (newDisplayName.Trim().Length > 128)
        {
            throw new ArgumentException("The display name cannot exceed 128 characters.", nameof(newDisplayName));
        }

        if (newMaximumAnnotatorConnections is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newMaximumAnnotatorConnections),
                "Maximum annotator connections must be between 1 and 16.");
        }

        displayName = newDisplayName.Trim();
        clientProfile = CreateClientProfile(profilePicturePath);
        maximumAnnotatorConnections = newMaximumAnnotatorConnections;

        var currentSessionId = SessionId;
        if (expectedRole != ClientRole.Host || currentSessionId is null)
        {
            return;
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await connection.InvokeAsync(
                "UpdateHostClientSettings",
                currentSessionId,
                displayName,
                clientProfile,
                maximumAnnotatorConnections,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ApproveAnnotatorAsync(
        string sessionId,
        string annotatorConnectionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await connection.InvokeAsync(
                "ApproveAnnotator",
                sessionId,
                annotatorConnectionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RejectAnnotatorAsync(
        string sessionId,
        string annotatorConnectionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await connection.InvokeAsync(
                "RejectAnnotator",
                sessionId,
                annotatorConnectionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DisconnectAllConnectionsAsync(
        CancellationToken cancellationToken = default)
    {
        var currentSessionId = SessionId
            ?? throw new InvalidOperationException("This host is not available on the relay.");
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await connection.InvokeAsync(
                "DisconnectAllConnections",
                currentSessionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DisconnectAnnotatorAsync(
        string annotatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(annotatorId);
        var currentSessionId = SessionId
            ?? throw new InvalidOperationException("This host is not available on the relay.");
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await connection.InvokeAsync(
                "DisconnectAnnotator",
                currentSessionId,
                annotatorId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetAnnotatorPausedAsync(
        string? annotatorId,
        bool paused,
        CancellationToken cancellationToken = default)
    {
        var currentSessionId = SessionId
            ?? throw new InvalidOperationException("This host is not available on the relay.");
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await connection.InvokeAsync(
                "SetAnnotatorPaused",
                currentSessionId,
                string.IsNullOrWhiteSpace(annotatorId) ? null : annotatorId,
                paused,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetAnnotationColorPreferenceAsync(
        string color,
        CancellationToken cancellationToken = default)
    {
        // Only an approved annotator has a colour to allocate. Before that the preference lives
        // in settings alone and is presented as soon as the session opens.
        if (!CanSend(ClientRole.Annotator, Credential))
        {
            return;
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await connection.InvokeAsync("SetAnnotationColorPreference", color, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> SendPointerAsync(
        PointerEventMessage pointerEvent,
        CancellationToken cancellationToken = default)
    {
        var currentCredential = Credential;
        if (!CanSend(ClientRole.Annotator, currentCredential)
            || !string.Equals(
                pointerEvent.SessionId,
                currentCredential!.SessionId,
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            await connection.InvokeAsync(
                    "SendPointer",
                    pointerEvent,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (connection.State != HubConnectionState.Connected)
        {
            return false;
        }
    }

    public async Task<bool> AcknowledgePointerAsync(
        PointerAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        if (!CanSend(ClientRole.Host, Credential))
        {
            return false;
        }

        try
        {
            await connection.InvokeAsync(
                    "AcknowledgePointer",
                    acknowledgement,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (connection.State != HubConnectionState.Connected)
        {
            return false;
        }
    }

    public async Task EndSessionAsync(CancellationToken cancellationToken = default)
    {
        var currentSessionId = SessionId;
        if (currentSessionId is null)
        {
            throw new InvalidOperationException("No active session is available to end.");
        }

        if (connection.State != HubConnectionState.Connected)
        {
            throw new InvalidOperationException(
                "The relay is disconnected, so disconnection could not be confirmed.");
        }

        await connection.InvokeAsync("EndSession", currentSessionId, cancellationToken)
            .ConfigureAwait(false);
        ClearSession();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        if (SessionId is not null && connection.State == HubConnectionState.Connected)
        {
            using var shutdownCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await EndSessionAsync(shutdownCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The server-side disconnect handler revokes peers when a graceful end
                // cannot be confirmed (for example, after a crash, network loss, or
                // a peer that already terminated the session).
            }
        }

        ClearSession();
        disposed = true;
        await connection.StopAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
        connectionGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void RegisterCallbacks()
    {
        connection.On(
            "HostDirectoryChanged",
            () => Publish(() => HostDirectoryChanged?.Invoke(this, EventArgs.Empty)));
        connection.On<AnnotatorDescriptor>(
            "AnnotatorJoinRequested",
            annotator => Publish(
                () => AnnotatorJoinRequested?.Invoke(
                    this,
                    new AnnotatorJoinRequestedEventArgs(annotator))));
        connection.On<string>(
            "AnnotatorJoinCancelled",
            annotatorConnectionId => Publish(
                () => AnnotatorJoinCancelled?.Invoke(
                    this,
                    new AnnotatorJoinCancelledEventArgs(annotatorConnectionId))));
        connection.On<SessionCredential>(
            "SessionCredentialIssued",
            issuedCredential => SetSession(issuedCredential.SessionId, issuedCredential));
        connection.On<SessionStateMessage>(
            "SessionApproved",
            state =>
            {
                if (Credential is { } credentialToPersist)
                {
                    PersistCredential(credentialToPersist);
                }

                Publish(
                    () => SessionApproved?.Invoke(this, new RelaySessionStateEventArgs(state)));
            });
        connection.On<DisplayDescriptor>(
            "HostDisplayChanged",
            display => Publish(
                () => HostDisplayChanged?.Invoke(
                    this,
                    new RelayHostDisplayChangedEventArgs(display))));
        connection.On<PointerEventMessage>(
            "PointerReceived",
            pointerEvent => Publish(
                () => PointerReceived?.Invoke(this, new RelayPointerEventArgs(pointerEvent))));
        connection.On<bool>(
            "AnnotationPaused",
            paused => Publish(
                () => AnnotationPausedChanged?.Invoke(
                    this,
                    new RelayAnnotationPausedEventArgs(paused))));
        connection.On<string>(
            "AnnotationColorAssigned",
            color => Publish(
                () => AnnotationColorAssigned?.Invoke(
                    this,
                    new RelayAnnotationColorEventArgs(color))));
        connection.On<PointerAcknowledgement>(
            "PointerDisplayed",
            acknowledgement => Publish(
                () => PointerDisplayed?.Invoke(
                    this,
                    new RelayAcknowledgementEventArgs(acknowledgement))));
        connection.On<string>(
            "SessionEnded",
            reason =>
            {
                var expired = reason.Contains("expired", StringComparison.OrdinalIgnoreCase);
                var endedSessionId = SessionId;
                ClearSession();
                auditLog?.Write(
                    ClientAuditEvent.SessionEnded,
                    sessionId: endedSessionId,
                    role: expectedRole);
                SetStatus(
                    expired ? RelayConnectionStatus.SessionExpired : RelayConnectionStatus.Connected,
                    reason);
                Publish(
                    () => SessionEnded?.Invoke(
                        this,
                        new RelaySessionEndedEventArgs(reason, expired)));
            });

        connection.Reconnecting += exception =>
        {
            // The relay tracks the room per connection, and a reconnect brings a new one that
            // starts in the default room, so this client has to name its room again before it
            // can see or reach anyone in it.
            lock (stateLock)
            {
                enteredRoom = null;
            }

            SetStatus(
                RelayConnectionStatus.Reconnecting,
                exception is null ? "Reconnecting to relay." : "Relay connection interrupted; reconnecting.");
            return Task.CompletedTask;
        };
        connection.Reconnected += OnReconnectedAsync;
        connection.Closed += exception =>
        {
            lock (stateLock)
            {
                enteredRoom = null;
            }

            if (!disposed)
            {
                SetStatus(
                    RelayConnectionStatus.Disconnected,
                    exception is null ? "Disconnected from relay." : "Relay connection failed.");
            }

            return Task.CompletedTask;
        };
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (connection.State == HubConnectionState.Connected && IsRoomEntryCurrent())
        {
            return;
        }

        await connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WaitForTransitionToSettleAsync(cancellationToken).ConfigureAwait(false);
            if (connection.State == HubConnectionState.Disconnected)
            {
                try
                {
                    await connection.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException exception)
                    when (exception.StatusCode == HttpStatusCode.Unauthorized)
                {
                    bool hasPassword;
                    lock (stateLock)
                    {
                        hasPassword = passwordKey is not null;
                    }

                    // The relay turned this client away at the front door. Reported apart from
                    // an unreachable relay, because the address is right and only the password
                    // is wrong — retrying without changing it cannot help.
                    SetStatus(
                        RelayConnectionStatus.Unauthorized,
                        hasPassword
                            ? "The server password is not correct."
                            : "This relay requires a server password.");
                    throw;
                }

                SetStatus(RelayConnectionStatus.Connected, "Connected to relay.");
            }

            await EnterRoomAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connectionGate.Release();
        }
    }

    /// <summary>
    /// The relay tracks the room per connection, so it is named again after every connect and
    /// reconnect.
    /// </summary>
    private async Task EnterRoomAsync(CancellationToken cancellationToken)
    {
        string? roomToEnter;
        lock (stateLock)
        {
            roomToEnter = GetRoomToEnterNoLock();
        }

        if (roomToEnter is null || connection.State != HubConnectionState.Connected)
        {
            return;
        }

        await connection.InvokeAsync("EnterRoom", roomToEnter, cancellationToken)
            .ConfigureAwait(false);
        lock (stateLock)
        {
            // This is now the room the relay holds for the connection. A room changed while the
            // call was in flight simply leaves one to name, and the comparison finds it.
            enteredRoom = roomToEnter;
        }
    }

    /// <summary>
    /// The room this connection still owes the relay, or null when the relay already holds the
    /// current one. A fresh connection owes its room even when it is the default one, because
    /// the relay is what decides where an unnamed connection sits.
    /// </summary>
    private string? GetRoomToEnterNoLock() =>
        string.Equals(room, enteredRoom, StringComparison.Ordinal) ? null : room;

    private bool IsRoomEntryCurrent()
    {
        lock (stateLock)
        {
            return GetRoomToEnterNoLock() is null;
        }
    }

    /// <summary>
    /// Automatic reconnect owns the connection while it is retrying, and starting it again in
    /// that state is not allowed. Waiting briefly lets a call that arrives during a short
    /// interruption succeed instead of failing with a transport error the caller reports to
    /// the user. A longer outage still falls through, because the retry schedule can run for
    /// far longer than a command should block.
    /// </summary>
    private async Task WaitForTransitionToSettleAsync(CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + TransitionSettleMilliseconds;
        while (connection.State
               is HubConnectionState.Connecting
               or HubConnectionState.Reconnecting)
        {
            if (Environment.TickCount64 >= deadline)
            {
                return;
            }

            await Task.Delay(TransitionPollMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OnReconnectedAsync(string? connectionId)
    {
        _ = connectionId;
        await EnterRoomAsync(CancellationToken.None).ConfigureAwait(false);
        var currentCredential = Credential;
        if (currentCredential is null)
        {
            SetStatus(RelayConnectionStatus.Connected, "Reconnected to relay.");
            return;
        }

        try
        {
            await ResumeSessionAsync(currentCredential, CancellationToken.None).ConfigureAwait(false);
            SetStatus(RelayConnectionStatus.Connected, "Reconnected and resumed session.");
            auditLog?.Write(
                ClientAuditEvent.SessionRestored,
                sessionId: currentCredential.SessionId,
                role: currentCredential.Role);
        }
        catch (Exception exception)
        {
            HandleResumeFailure(exception);
        }
    }

    private async Task ResumeSessionAsync(
        SessionCredential currentCredential,
        CancellationToken cancellationToken)
    {
        var resumedCredential = await connection.InvokeAsync<SessionCredential>(
                "ResumeSession",
                new SessionResumeRequest(
                    currentCredential.SessionId,
                    currentCredential.Role,
                    currentCredential.ClientInstanceId,
                    currentCredential.SessionToken,
                    currentCredential.ReconnectToken),
                cancellationToken)
            .ConfigureAwait(false);
        SetSession(resumedCredential.SessionId, resumedCredential);
    }

    private void HandleResumeFailure(Exception exception)
    {
        var currentCredential = Credential;
        auditLog?.Write(
            ClientAuditEvent.SessionRestoreFailed,
            ClientAuditLevel.Warning,
            currentCredential?.SessionId,
            currentCredential?.Role ?? expectedRole,
            exception: exception);
        ClearSession();
        SetStatus(
            RelayConnectionStatus.SessionExpired,
            "The previous session could not be resumed.");
        Publish(
            () => SessionEnded?.Invoke(
                this,
                new RelaySessionEndedEventArgs(
                    "The previous session could not be resumed.",
                    expired: true)));
    }

    private bool CanSend(ClientRole role, SessionCredential? currentCredential) =>
        Status == RelayConnectionStatus.Connected
        && connection.State == HubConnectionState.Connected
        && currentCredential?.Role == role;

    private void DiscardRecoveredCredential(ClientRole role)
    {
        if (expectedRole == role && Credential is not null)
        {
            ClearSession();
        }
    }

    private void SetSession(string newSessionId, SessionCredential newCredential)
    {
        if (expectedRole is not null && newCredential.Role != expectedRole)
        {
            throw new InvalidOperationException("The relay issued a credential for the wrong role.");
        }

        lock (stateLock)
        {
            sessionId = newSessionId;
            credential = newCredential;
        }

        PersistCredential(newCredential);
    }

    private void PersistCredential(SessionCredential credentialToPersist)
    {
        if (sessionStore is null)
        {
            return;
        }

        try
        {
            sessionStore.Save(credentialToPersist);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException)
        {
            auditLog?.Write(
                ClientAuditEvent.SessionCredentialProtectionFailed,
                ClientAuditLevel.Warning,
                credentialToPersist.SessionId,
                credentialToPersist.Role,
                exception: exception);
        }
    }

    private void ClearSession()
    {
        ClientRole? role;
        lock (stateLock)
        {
            role = credential?.Role ?? expectedRole;
            sessionId = null;
            credential = null;
        }

        if (role is not null && sessionStore is not null)
        {
            try
            {
                sessionStore.Clear(role.Value);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                auditLog?.Write(
                    ClientAuditEvent.SessionCredentialProtectionFailed,
                    ClientAuditLevel.Warning,
                    role: role,
                    exception: exception);
            }
        }
    }

    private void SetStatus(RelayConnectionStatus newStatus, string message)
    {
        lock (stateLock)
        {
            status = newStatus;
        }

        auditLog?.Write(
            ClientAuditEvent.ConnectionStateChanged,
            newStatus is RelayConnectionStatus.Disconnected or RelayConnectionStatus.SessionExpired
                ? ClientAuditLevel.Warning
                : ClientAuditLevel.Information,
            SessionId,
            expectedRole,
            newStatus);

        Publish(
            () => ConnectionStatusChanged?.Invoke(
                this,
                new RelayConnectionStatusChangedEventArgs(newStatus, message)));
    }

    private void Publish(Action action)
    {
        if (synchronizationContext is null)
        {
            action();
            return;
        }

        synchronizationContext.Post(_ => action(), null);
    }

    private static string GetClientVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "1.0.0" : version.ToString(3);
    }

    private static ClientProfile CreateClientProfile(string? picturePath)
    {
        if (string.IsNullOrWhiteSpace(picturePath) || !File.Exists(picturePath))
        {
            return new ClientProfile();
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 64;
            bitmap.UriSource = new Uri(picturePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.Length <= ContractValidator.MaximumProfilePictureBytes
                ? new ClientProfile(stream.ToArray())
                : new ClientProfile();
        }
        catch (Exception exception) when (
            exception is IOException
                or ArgumentException
                or FormatException
                or InvalidOperationException
                or NotSupportedException
                or UnauthorizedAccessException
                or SecurityException)
        {
            // A picture that cannot be decoded is not worth failing over: this runs from the
            // constructor, so an unhandled failure here would stop the client from starting.
            return new ClientProfile();
        }
    }

    private static ClientSettings ValidateProductionSettings(ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return settings;
    }

    private static void EnsureValid(ValidationResult result)
    {
        if (!result.IsValid)
        {
            throw new ArgumentException(result.Errors[0].Message);
        }
    }
}
