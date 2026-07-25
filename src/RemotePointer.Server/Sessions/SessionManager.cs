using Microsoft.Extensions.Options;
using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Validation;
using RemotePointer.Server.RateLimiting;

namespace RemotePointer.Server.Sessions;

public sealed class SessionManager : ISessionManager
{
    private readonly Dictionary<string, ConnectionMembership> connections = new(StringComparer.Ordinal);
    private readonly object syncRoot = new();
    private readonly Dictionary<string, string> pairingCodeSessions = new(StringComparer.Ordinal);
    private readonly PointerRateLimitOptions rateLimitOptions;
    private readonly ISessionSecretGenerator secretGenerator;
    private readonly SessionOptions sessionOptions;
    private readonly Dictionary<string, SessionRecord> sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;

    public SessionManager(
        IOptions<SessionOptions> sessionOptions,
        IOptions<PointerRateLimitOptions> rateLimitOptions,
        ISessionSecretGenerator secretGenerator,
        TimeProvider timeProvider)
    {
        this.sessionOptions = sessionOptions?.Value
            ?? throw new ArgumentNullException(nameof(sessionOptions));
        this.rateLimitOptions = rateLimitOptions?.Value
            ?? throw new ArgumentNullException(nameof(rateLimitOptions));
        this.secretGenerator = secretGenerator
            ?? throw new ArgumentNullException(nameof(secretGenerator));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        ValidateOptions(this.sessionOptions, this.rateLimitOptions);
    }

    public int ActiveSessionCount
    {
        get
        {
            lock (syncRoot)
            {
                return sessions.Count;
            }
        }
    }

    public bool ReceiverDiscoveryEnabled => sessionOptions.ReceiverDiscoveryEnabled;

    public CreateSessionResponse CreateReceiverSession(
        DisplayDescriptor display,
        string connectionId,
        string clientInstanceId,
        string receiverDisplayName,
        string? applicationInstanceId = null,
        ClientProfile? profile = null,
        int maximumPresenterConnections = 2)
    {
        applicationInstanceId = string.IsNullOrWhiteSpace(applicationInstanceId)
            ? clientInstanceId
            : applicationInstanceId;
        profile ??= new ClientProfile();
        EnsureIdentifier(connectionId, nameof(connectionId));
        EnsureIdentifier(clientInstanceId, nameof(clientInstanceId));
        EnsureIdentifier(applicationInstanceId, nameof(applicationInstanceId));
        EnsureIdentifier(receiverDisplayName, nameof(receiverDisplayName));
        EnsureValid(ContractValidator.Validate(display), "invalid_display");
        EnsureValid(ContractValidator.Validate(profile), "invalid_profile");
        if (maximumPresenterConnections < 1
            || maximumPresenterConnections > sessionOptions.MaximumPresentersPerReceiver)
        {
            throw new SessionOperationException(
                "invalid_presenter_limit",
                $"Maximum presenter connections must be between 1 and {sessionOptions.MaximumPresentersPerReceiver}.");
        }

        lock (syncRoot)
        {
            EnsureConnectionIsUnbound(connectionId);
            var now = timeProvider.GetUtcNow();
            var sessionId = GenerateUniqueSessionId();
            var pairingCode = GenerateUniquePairingCode();
            var pairingCodeHash = secretGenerator.HashSecret(pairingCode);
            var sessionSecret = secretGenerator.GenerateSecret();
            var sessionToken = secretGenerator.GenerateSecret();
            var reconnectToken = secretGenerator.GenerateSecret();
            var expiresAt = now.AddHours(sessionOptions.MaximumSessionHours);

            var receiver = new Participant(
                ClientRole.Receiver,
                clientInstanceId,
                connectionId,
                secretGenerator.HashSecret(sessionToken),
                secretGenerator.HashSecret(reconnectToken));
            var session = new SessionRecord(
                sessionId,
                pairingCodeHash,
                now.AddMinutes(sessionOptions.PairingCodeLifetimeMinutes),
                expiresAt,
                secretGenerator.HashSecret(sessionSecret),
                display,
                receiverDisplayName,
                applicationInstanceId,
                profile.PicturePng is null ? null : [.. profile.PicturePng],
                receiver,
                sessionOptions.SequenceWindowSize,
                maximumPresenterConnections);
            session.IsDiscoverable = sessionOptions.ReceiverDiscoveryEnabled;

            sessions.Add(sessionId, session);
            pairingCodeSessions.Add(pairingCodeHash, sessionId);
            connections.Add(
                connectionId,
                new ConnectionMembership(sessionId, ClientRole.Receiver, Approved: true));

            var credential = new SessionCredential(
                sessionId,
                ClientRole.Receiver,
                clientInstanceId,
                sessionToken,
                reconnectToken,
                expiresAt);
            return new CreateSessionResponse(
                sessionId,
                pairingCode,
                sessionSecret,
                credential,
                session.PairingCodeExpiresAt);
        }
    }

    public IReadOnlyList<AvailableReceiverDescriptor> GetAvailableReceivers(
        string? excludedApplicationInstanceId = null)
    {
        if (!sessionOptions.ReceiverDiscoveryEnabled)
        {
            return [];
        }

        lock (syncRoot)
        {
            var now = timeProvider.GetUtcNow();
            return sessions.Values
                .Where(session =>
                    session.IsDiscoverable
                    && session.ExpiresAt > now
                    && session.Receiver.ConnectionId is not null
                    && session.PendingPresenter is null
                    && session.Presenters.Count < session.MaximumPresenterConnections
                    && !string.Equals(
                        session.ApplicationInstanceId,
                        excludedApplicationInstanceId,
                        StringComparison.Ordinal))
                .OrderBy(session => session.ReceiverDisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(session => new AvailableReceiverDescriptor(
                    session.Id,
                    session.ReceiverDisplayName,
                    session.ApplicationInstanceId,
                    session.ProfilePicturePng is null
                        ? null
                        : [.. session.ProfilePicturePng]))
                .ToArray();
        }
    }

    public bool SetReceiverDiscoverable(
        string sessionId,
        string receiverConnectionId,
        bool discoverable)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(receiverConnectionId, nameof(receiverConnectionId));
        if (!sessionOptions.ReceiverDiscoveryEnabled)
        {
            throw new SessionOperationException(
                "receiver_discovery_disabled",
                "Receiver discovery is disabled on this relay.");
        }

        lock (syncRoot)
        {
            var session = GetActiveSession(sessionId);
            EnsureMembership(
                receiverConnectionId,
                sessionId,
                ClientRole.Receiver,
                requireApproved: true);
            session.IsDiscoverable = discoverable;
            return session.IsDiscoverable;
        }
    }

    public JoinSessionResult RequestToJoinSession(
        JoinRequest request,
        string connectionId,
        string displayName,
        string? applicationInstanceId = null)
    {
        EnsureIdentifier(connectionId, nameof(connectionId));
        EnsureIdentifier(displayName, nameof(displayName));
        var validation = ContractValidator.Validate(request);
        if (!validation.IsValid || request.Role != ClientRole.Presenter)
        {
            return RejectedJoin("The join request is invalid.");
        }

        lock (syncRoot)
        {
            if (connections.ContainsKey(connectionId))
            {
                return RejectedJoin("This connection is already participating in a session.");
            }

            var normalizedCode = PairingCodeValidator.Normalize(request.PairingCode);
            var pairingHash = secretGenerator.HashSecret(normalizedCode);
            if (!pairingCodeSessions.TryGetValue(pairingHash, out var sessionId)
                || !sessions.TryGetValue(sessionId, out var session))
            {
                return RejectedJoin("The pairing code is invalid or expired.");
            }

            var now = timeProvider.GetUtcNow();
            if (session.ExpiresAt <= now)
            {
                _ = TerminateSessionNoLock(session);
                return RejectedJoin("The pairing code is invalid or expired.");
            }

            if (session.PairingCodeExpiresAt <= now)
            {
                pairingCodeSessions.Remove(session.PairingCodeHash);
                session.PairingCodeConsumed = true;
                if (!session.IsDiscoverable)
                {
                    _ = TerminateSessionNoLock(session);
                }

                return RejectedJoin("The pairing code is invalid or expired.");
            }

            return BindPendingPresenterNoLock(
                session,
                connectionId,
                request.ClientInstanceId,
                applicationInstanceId,
                displayName,
                request.ClientVersion,
                request.Profile?.PicturePng);
        }
    }

    public JoinSessionResult RequestToJoinReceiver(
        DirectJoinRequest request,
        string connectionId,
        string displayName,
        string? applicationInstanceId = null)
    {
        EnsureIdentifier(connectionId, nameof(connectionId));
        EnsureIdentifier(displayName, nameof(displayName));
        if (!sessionOptions.ReceiverDiscoveryEnabled)
        {
            return RejectedJoin("Receiver discovery is disabled on this relay.");
        }

        var validation = ContractValidator.Validate(request);
        if (!validation.IsValid)
        {
            return RejectedJoin("The direct join request is invalid.");
        }

        lock (syncRoot)
        {
            if (connections.ContainsKey(connectionId))
            {
                return RejectedJoin("This connection is already participating in a session.");
            }

            if (!sessions.TryGetValue(request.SessionId, out var session)
                || !session.IsDiscoverable
                || session.ExpiresAt <= timeProvider.GetUtcNow()
                || session.Receiver.ConnectionId is null)
            {
                return RejectedJoin("The selected receiver is no longer available.");
            }

            return BindPendingPresenterNoLock(
                session,
                connectionId,
                request.ClientInstanceId,
                applicationInstanceId,
                displayName,
                request.ClientVersion,
                request.Profile?.PicturePng);
        }
    }

    public ReceiverDisplayUpdateResult UpdateReceiverDisplay(
        string sessionId,
        string receiverConnectionId,
        DisplayDescriptor display)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(receiverConnectionId, nameof(receiverConnectionId));
        EnsureValid(ContractValidator.Validate(display), "invalid_display");

        lock (syncRoot)
        {
            var session = GetActiveSession(sessionId);
            EnsureMembership(
                receiverConnectionId,
                sessionId,
                ClientRole.Receiver,
                requireApproved: true);
            session.ReceiverDisplay = display;
            return new ReceiverDisplayUpdateResult(
                session.Id,
                session.Presenters.Values
                    .Select(presenter => presenter.Participant.ConnectionId)
                    .OfType<string>()
                    .ToArray(),
                display);
        }
    }

    public ReceiverClientSettingsUpdateResult UpdateReceiverClientSettings(
        string sessionId,
        string receiverConnectionId,
        string receiverDisplayName,
        ClientProfile profile,
        int maximumPresenterConnections)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(receiverConnectionId, nameof(receiverConnectionId));
        EnsureIdentifier(receiverDisplayName, nameof(receiverDisplayName));
        EnsureValid(ContractValidator.Validate(profile), "invalid_profile");
        if (maximumPresenterConnections < 1
            || maximumPresenterConnections > sessionOptions.MaximumPresentersPerReceiver)
        {
            throw new SessionOperationException(
                "invalid_presenter_limit",
                $"Maximum presenter connections must be between 1 and {sessionOptions.MaximumPresentersPerReceiver}.");
        }

        lock (syncRoot)
        {
            var session = GetActiveSession(sessionId);
            EnsureMembership(
                receiverConnectionId,
                sessionId,
                ClientRole.Receiver,
                requireApproved: true);
            session.ReceiverDisplayName = receiverDisplayName;
            session.ProfilePicturePng = profile.PicturePng is null
                ? null
                : [.. profile.PicturePng];
            session.MaximumPresenterConnections = maximumPresenterConnections;
            return new ReceiverClientSettingsUpdateResult(
                receiverConnectionId,
                session.Presenters.Values
                    .Select(presenter => presenter.Participant.ConnectionId)
                    .OfType<string>()
                    .ToArray(),
                CreateState(session));
        }
    }

    public ApprovePresenterResult ApprovePresenter(
        string sessionId,
        string presenterConnectionId,
        string receiverConnectionId)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(presenterConnectionId, nameof(presenterConnectionId));
        EnsureIdentifier(receiverConnectionId, nameof(receiverConnectionId));

        lock (syncRoot)
        {
            var session = GetActiveSession(sessionId);
            EnsureMembership(
                receiverConnectionId,
                sessionId,
                ClientRole.Receiver,
                requireApproved: true);

            var pending = session.PendingPresenter;
            if (pending is null
                || !string.Equals(
                    pending.ConnectionId,
                    presenterConnectionId,
                    StringComparison.Ordinal))
            {
                throw new SessionOperationException(
                    "presenter_not_pending",
                    "The selected presenter no longer has a pending request.");
            }

            var sessionToken = secretGenerator.GenerateSecret();
            var reconnectToken = secretGenerator.GenerateSecret();
            var participant = new Participant(
                ClientRole.Presenter,
                pending.ClientInstanceId,
                presenterConnectionId,
                secretGenerator.HashSecret(sessionToken),
                secretGenerator.HashSecret(reconnectToken));
            session.Presenters.Add(
                presenterConnectionId,
                new ConnectedPresenter(
                    pending,
                    participant,
                    new SequenceNumberTracker(session.SequenceWindowSize),
                    new PointerTokenBucket(
                        rateLimitOptions.EventsPerSecond,
                        rateLimitOptions.BurstSize,
                        timeProvider.GetUtcNow())));
            session.PendingPresenter = null;
            connections[presenterConnectionId] = new ConnectionMembership(
                session.Id,
                ClientRole.Presenter,
                Approved: true);

            var credential = new SessionCredential(
                session.Id,
                ClientRole.Presenter,
                pending.ClientInstanceId,
                sessionToken,
                reconnectToken,
                session.ExpiresAt);
            var state = CreateState(session);
            return new ApprovePresenterResult(
                session.Id,
                presenterConnectionId,
                receiverConnectionId,
                credential,
                state);
        }
    }

    public RejectPresenterResult RejectPresenter(
        string sessionId,
        string presenterConnectionId,
        string receiverConnectionId)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(presenterConnectionId, nameof(presenterConnectionId));
        EnsureIdentifier(receiverConnectionId, nameof(receiverConnectionId));

        lock (syncRoot)
        {
            var session = GetActiveSession(sessionId);
            EnsureMembership(
                receiverConnectionId,
                sessionId,
                ClientRole.Receiver,
                requireApproved: true);
            var pending = session.PendingPresenter;
            if (pending is null
                || !string.Equals(
                    pending.ConnectionId,
                    presenterConnectionId,
                    StringComparison.Ordinal))
            {
                throw new SessionOperationException(
                    "presenter_not_pending",
                    "The selected presenter no longer has a pending request.");
            }

            session.PendingPresenter = null;
            connections.Remove(presenterConnectionId);
            return new RejectPresenterResult(
                sessionId,
                presenterConnectionId,
                receiverConnectionId);
        }
    }

    public PointerRelayResult AcceptPointer(
        string connectionId,
        PointerEventMessage pointerEvent)
    {
        EnsureIdentifier(connectionId, nameof(connectionId));
        var now = timeProvider.GetUtcNow();
        EnsureValid(ContractValidator.Validate(pointerEvent, now), "invalid_pointer");

        lock (syncRoot)
        {
            var membership = GetMembership(connectionId);
            if (membership.Role != ClientRole.Presenter || !membership.Approved)
            {
                throw new SessionOperationException(
                    "presenter_required",
                    "Only the approved presenter can send pointer events.");
            }

            if (!string.Equals(membership.SessionId, pointerEvent.SessionId, StringComparison.Ordinal))
            {
                throw new SessionOperationException(
                    "session_mismatch",
                    "The pointer event does not belong to this connection's session.");
            }

            var session = GetActiveSession(membership.SessionId);
            if (!session.Presenters.TryGetValue(connectionId, out var presenter))
            {
                throw new SessionOperationException(
                    "presenter_not_connected",
                    "The presenter is no longer connected to this session.");
            }
            // The budget is per presenter: each one sends its own gesture stream at the
            // client's update rate, so a shared session budget would throttle everybody as
            // soon as two senders drew at the same time.
            if (!presenter.RateLimiter.TryAcquire(now))
            {
                throw new SessionOperationException(
                    "pointer_rate_exceeded",
                    "The pointer event rate limit was exceeded.");
            }

            if (!presenter.SequenceNumbers.TryAccept(pointerEvent.SequenceNumber))
            {
                return new PointerRelayResult(
                    PointerRelayDisposition.IgnoredSequence,
                    session.Id,
                    session.Receiver.ConnectionId);
            }

            session.PointerCount++;
            session.RecordPointerOrigin(pointerEvent.EventId, connectionId);
            return new PointerRelayResult(
                PointerRelayDisposition.Accepted,
                session.Id,
                session.Receiver.ConnectionId);
        }
    }

    public AcknowledgementRelayResult AcceptAcknowledgement(
        string connectionId,
        PointerAcknowledgement acknowledgement)
    {
        EnsureIdentifier(connectionId, nameof(connectionId));
        EnsureValid(ContractValidator.Validate(acknowledgement), "invalid_acknowledgement");

        lock (syncRoot)
        {
            var membership = GetMembership(connectionId);
            if (membership.Role != ClientRole.Receiver || !membership.Approved)
            {
                throw new SessionOperationException(
                    "receiver_required",
                    "Only the session receiver can acknowledge pointer events.");
            }

            var session = GetActiveSession(membership.SessionId);
            return new AcknowledgementRelayResult(
                session.Id,
                session.TakePointerOrigin(acknowledgement.EventId));
        }
    }

    public ResumeSessionResult ResumeSession(
        string connectionId,
        SessionResumeRequest request,
        string? applicationInstanceId = null)
    {
        EnsureIdentifier(connectionId, nameof(connectionId));
        EnsureValid(ContractValidator.Validate(request), "invalid_resume_request");

        lock (syncRoot)
        {
            EnsureConnectionIsUnbound(connectionId);
            var session = GetActiveSession(request.SessionId);
            ConnectedPresenter? connectedPresenter = null;
            Participant? participant;
            if (request.Role == ClientRole.Receiver)
            {
                participant = session.Receiver;
            }
            else if (request.Role == ClientRole.Presenter)
            {
                connectedPresenter = session.Presenters.Values.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.Participant.ClientInstanceId,
                        request.ClientInstanceId,
                        StringComparison.Ordinal)
                    && secretGenerator.SecretMatches(
                        request.SessionToken,
                        candidate.Participant.SessionTokenHash)
                    && secretGenerator.SecretMatches(
                        request.ReconnectToken,
                        candidate.Participant.ReconnectTokenHash));
                participant = connectedPresenter?.Participant;
            }
            else
            {
                participant = null;
            }

            if (participant is null
                || !string.Equals(
                    participant.ClientInstanceId,
                    request.ClientInstanceId,
                    StringComparison.Ordinal)
                || !secretGenerator.SecretMatches(request.SessionToken, participant.SessionTokenHash)
                || !secretGenerator.SecretMatches(request.ReconnectToken, participant.ReconnectTokenHash))
            {
                throw new SessionOperationException(
                    "resume_denied",
                    "The session credentials are invalid or expired.");
            }

            var replacedConnectionId = participant.ConnectionId;
            if (replacedConnectionId is not null)
            {
                connections.Remove(replacedConnectionId);
            }

            var newReconnectToken = secretGenerator.GenerateSecret();
            participant.ConnectionId = connectionId;
            participant.ReconnectTokenHash = secretGenerator.HashSecret(newReconnectToken);
            if (connectedPresenter is not null)
            {
                var previousPresenterKey = session.Presenters
                    .First(pair => ReferenceEquals(pair.Value, connectedPresenter))
                    .Key;
                if (!string.Equals(previousPresenterKey, connectionId, StringComparison.Ordinal))
                {
                    session.Presenters.Remove(previousPresenterKey);
                }

                session.ReplacePointerOriginConnection(previousPresenterKey, connectionId);
                connectedPresenter.Descriptor = connectedPresenter.Descriptor with
                {
                    ConnectionId = connectionId,
                };
                session.Presenters[connectionId] = connectedPresenter;
            }
            if (participant.Role == ClientRole.Receiver
                && !string.IsNullOrWhiteSpace(applicationInstanceId))
            {
                EnsureIdentifier(applicationInstanceId, nameof(applicationInstanceId));
                session.ApplicationInstanceId = applicationInstanceId;
            }
            connections.Add(
                connectionId,
                new ConnectionMembership(session.Id, participant.Role, Approved: true));

            var credential = new SessionCredential(
                session.Id,
                participant.Role,
                participant.ClientInstanceId,
                request.SessionToken,
                newReconnectToken,
                session.ExpiresAt);
            return new ResumeSessionResult(
                credential,
                CreateState(session),
                replacedConnectionId);
        }
    }

    public SessionTerminationResult EndSession(string sessionId, string connectionId)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(connectionId, nameof(connectionId));

        lock (syncRoot)
        {
            var membership = GetMembership(connectionId);
            if (!string.Equals(membership.SessionId, sessionId, StringComparison.Ordinal)
                || (!membership.Approved && membership.Role != ClientRole.Presenter))
            {
                throw new SessionOperationException(
                    "session_member_required",
                    "Only a session member can end the session.");
            }

            var session = GetActiveSession(sessionId);
            if (membership.Role == ClientRole.Presenter)
            {
                // A presenter waiting for approval is still bound to the session, so it can
                // withdraw its own request without waiting for the receiver to answer.
                return membership.Approved
                    ? DisconnectPresenterNoLock(session, connectionId)
                    : CancelPendingPresenterNoLock(session, connectionId);
            }

            return TerminateSessionNoLock(session);
        }
    }

    public SessionTerminationResult DisconnectPresenters(
        string sessionId,
        string receiverConnectionId)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(receiverConnectionId, nameof(receiverConnectionId));

        lock (syncRoot)
        {
            var session = GetActiveSession(sessionId);
            EnsureMembership(
                receiverConnectionId,
                sessionId,
                ClientRole.Receiver,
                requireApproved: true);
            if (session.Presenters.Count == 0 && session.PendingPresenter is null)
            {
                throw new SessionOperationException(
                    "presenter_not_connected",
                    "No presenter is connected to this receiver.");
            }

            return DisconnectPresentersNoLock(session);
        }
    }

    public IReadOnlyList<SessionTerminationResult> CollectExpiredSessions()
    {
        lock (syncRoot)
        {
            var now = timeProvider.GetUtcNow();
            var terminated = new List<SessionTerminationResult>();
            foreach (var session in sessions.Values.ToArray())
            {
                if (session.ExpiresAt <= now)
                {
                    terminated.Add(TerminateSessionNoLock(session));
                    continue;
                }

                if (!session.PairingCodeConsumed && session.PairingCodeExpiresAt <= now)
                {
                    if (session.IsDiscoverable)
                    {
                        pairingCodeSessions.Remove(session.PairingCodeHash);
                        session.PairingCodeConsumed = true;
                    }
                    else
                    {
                        terminated.Add(TerminateSessionNoLock(session));
                    }
                }
            }

            return terminated;
        }
    }

    public ConnectionDisconnectResult? Disconnect(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return null;
        }

        lock (syncRoot)
        {
            if (!connections.TryGetValue(connectionId, out var membership)
                || !sessions.TryGetValue(membership.SessionId, out var session))
            {
                return null;
            }

            if (membership.Role == ClientRole.Receiver)
            {
                connections.Remove(connectionId);
                session.Receiver.ConnectionId = null;

                var presenterConnectionIds = session.Presenters.Values
                    .Select(presenter => presenter.Participant.ConnectionId)
                    .OfType<string>()
                    .Concat(session.PendingPresenter is null
                        ? []
                        : [session.PendingPresenter.ConnectionId])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                foreach (var presenterConnectionId in presenterConnectionIds)
                {
                    connections.Remove(presenterConnectionId);
                }

                session.Presenters.Clear();
                session.PendingPresenter = null;
                session.ClearPointerOrigins();
                return new ConnectionDisconnectResult(
                    session.Id,
                    ClientRole.Receiver,
                    presenterConnectionIds,
                    ReceiverConnectionId: null,
                    CreateState(session));
            }

            connections.Remove(connectionId);
            var cancelledPresenterRequestConnectionId = membership.Approved
                ? null
                : connectionId;
            if (!membership.Approved)
            {
                if (session.PendingPresenter?.ConnectionId == connectionId)
                {
                    session.PendingPresenter = null;
                }
            }
            else
            {
                session.Presenters.Remove(connectionId);
                session.RemovePointerOrigins(connectionId);
            }

            return new ConnectionDisconnectResult(
                session.Id,
                ClientRole.Presenter,
                PresenterConnectionIdsToEnd: [],
                session.Receiver.ConnectionId,
                CreateState(session),
                cancelledPresenterRequestConnectionId);
        }
    }

    private static void ValidateOptions(
        SessionOptions sessionOptions,
        PointerRateLimitOptions rateLimitOptions)
    {
        if (sessionOptions.PairingCodeLifetimeMinutes <= 0
            || sessionOptions.MaximumSessionHours <= 0
            || sessionOptions.MaximumSessionHours > 8
            || sessionOptions.SequenceWindowSize <= 0
            || sessionOptions.MaximumPresentersPerReceiver is < 1
                or > ContractValidator.MaximumConnectedPresenters
            || rateLimitOptions.EventsPerSecond <= 0
            || rateLimitOptions.BurstSize <= 0)
        {
            throw new InvalidOperationException("Session and pointer-rate options are invalid.");
        }
    }

    private static void EnsureIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new ArgumentException("A non-empty identifier of at most 128 characters is required.", parameterName);
        }
    }

    private static void EnsureValid(ValidationResult result, string code)
    {
        if (!result.IsValid)
        {
            throw new SessionOperationException(code, result.Errors[0].Message);
        }
    }

    private static JoinSessionResult RejectedJoin(string reason) => new(
        new JoinResponse(false, null, reason),
        null,
        null);

    private JoinSessionResult BindPendingPresenterNoLock(
        SessionRecord session,
        string connectionId,
        string clientInstanceId,
        string? applicationInstanceId,
        string displayName,
        string clientVersion,
        byte[]? profilePicturePng)
    {
        applicationInstanceId = string.IsNullOrWhiteSpace(applicationInstanceId)
            ? clientInstanceId
            : applicationInstanceId;
        if (string.Equals(
                session.ApplicationInstanceId,
                applicationInstanceId,
                StringComparison.Ordinal))
        {
            return RejectedJoin("A client cannot connect to itself.");
        }

        if (session.Presenters.Count >= session.MaximumPresenterConnections)
        {
            return RejectedJoin("The receiver has reached its connection limit.");
        }

        if (session.PendingPresenter is not null)
        {
            return RejectedJoin("The session already has a presenter request.");
        }

        session.PairingCodeConsumed = true;
        pairingCodeSessions.Remove(session.PairingCodeHash);
        var presenter = new PresenterDescriptor(
            connectionId,
            clientInstanceId,
            displayName,
            clientVersion,
            profilePicturePng is null ? null : [.. profilePicturePng]);
        session.PendingPresenter = presenter;
        connections.Add(
            connectionId,
            new ConnectionMembership(session.Id, ClientRole.Presenter, Approved: false));

        return new JoinSessionResult(
            new JoinResponse(true, session.Id, null),
            session.Receiver.ConnectionId,
            presenter);
    }

    private string GenerateUniqueSessionId()
    {
        string sessionId;
        do
        {
            sessionId = secretGenerator.GenerateIdentifier();
        }
        while (sessions.ContainsKey(sessionId));

        return sessionId;
    }

    private string GenerateUniquePairingCode()
    {
        string pairingCode;
        string pairingHash;
        do
        {
            pairingCode = secretGenerator.GeneratePairingCode();
            pairingHash = secretGenerator.HashSecret(pairingCode);
        }
        while (pairingCodeSessions.ContainsKey(pairingHash));

        return pairingCode;
    }

    private void EnsureConnectionIsUnbound(string connectionId)
    {
        if (connections.ContainsKey(connectionId))
        {
            throw new SessionOperationException(
                "connection_already_bound",
                "This connection already belongs to a session.");
        }
    }

    private ConnectionMembership GetMembership(string connectionId)
    {
        if (!connections.TryGetValue(connectionId, out var membership))
        {
            throw new SessionOperationException(
                "connection_not_authorized",
                "This connection is not authorized for a session.");
        }

        return membership;
    }

    private void EnsureMembership(
        string connectionId,
        string sessionId,
        ClientRole role,
        bool requireApproved)
    {
        var membership = GetMembership(connectionId);
        if (!string.Equals(membership.SessionId, sessionId, StringComparison.Ordinal)
            || membership.Role != role
            || (requireApproved && !membership.Approved))
        {
            throw new SessionOperationException(
                "role_not_authorized",
                "This connection is not authorized for the requested session action.");
        }
    }

    private SessionRecord GetActiveSession(string sessionId)
    {
        if (!sessions.TryGetValue(sessionId, out var session))
        {
            throw new SessionOperationException("session_not_found", "The session does not exist.");
        }

        if (session.ExpiresAt <= timeProvider.GetUtcNow())
        {
            _ = TerminateSessionNoLock(session);
            throw new SessionOperationException("session_expired", "The session has expired.");
        }

        return session;
    }

    private SessionTerminationResult TerminateSessionNoLock(SessionRecord session)
    {
        sessions.Remove(session.Id);
        pairingCodeSessions.Remove(session.PairingCodeHash);
        var connectionIds = connections
            .Where(pair => string.Equals(pair.Value.SessionId, session.Id, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var connectionId in connectionIds)
        {
            connections.Remove(connectionId);
        }

        return new SessionTerminationResult(session.Id, connectionIds, session.PointerCount);
    }

    private SessionTerminationResult DisconnectPresentersNoLock(SessionRecord session)
    {
        var presenterConnectionIds = session.Presenters.Values
            .Select(presenter => presenter.Participant.ConnectionId)
            .OfType<string>()
            .Concat(session.PendingPresenter is null
                ? []
                : [session.PendingPresenter.ConnectionId])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var presenterConnectionId in presenterConnectionIds)
        {
            connections.Remove(presenterConnectionId);
        }

        session.Presenters.Clear();
        session.PendingPresenter = null;
        session.ClearPointerOrigins();
        return new SessionTerminationResult(
            session.Id,
            presenterConnectionIds,
            session.PointerCount,
            ReceiverPreserved: true,
            PresenterConnectionId: presenterConnectionIds.FirstOrDefault(),
            ReceiverConnectionId: session.Receiver.ConnectionId,
            State: CreateState(session),
            PresenterConnectionIds: presenterConnectionIds);
    }

    private SessionTerminationResult CancelPendingPresenterNoLock(
        SessionRecord session,
        string presenterConnectionId)
    {
        if (session.PendingPresenter is null
            || !string.Equals(
                session.PendingPresenter.ConnectionId,
                presenterConnectionId,
                StringComparison.Ordinal))
        {
            throw new SessionOperationException(
                "presenter_not_pending",
                "This connection no longer has a pending request.");
        }

        connections.Remove(presenterConnectionId);
        session.PendingPresenter = null;
        return new SessionTerminationResult(
            session.Id,
            [presenterConnectionId],
            session.PointerCount,
            ReceiverPreserved: true,
            PresenterConnectionId: presenterConnectionId,
            ReceiverConnectionId: session.Receiver.ConnectionId,
            State: CreateState(session),
            PresenterConnectionIds: [presenterConnectionId],
            CancelledPresenterRequestConnectionId: presenterConnectionId);
    }

    private SessionTerminationResult DisconnectPresenterNoLock(
        SessionRecord session,
        string presenterConnectionId)
    {
        connections.Remove(presenterConnectionId);
        session.Presenters.Remove(presenterConnectionId);
        session.RemovePointerOrigins(presenterConnectionId);
        return new SessionTerminationResult(
            session.Id,
            [presenterConnectionId],
            session.PointerCount,
            ReceiverPreserved: true,
            PresenterConnectionId: presenterConnectionId,
            ReceiverConnectionId: session.Receiver.ConnectionId,
            State: CreateState(session),
            PresenterConnectionIds: [presenterConnectionId]);
    }

    private static SessionStateMessage CreateState(SessionRecord session) => new(
        session.Id,
        Approved: session.Presenters.Count > 0,
        session.ReceiverDisplay,
        session.ExpiresAt,
        session.IsDiscoverable,
        session.Presenters.Values
            .Select(presenter => new ConnectedPresenterDescriptor(
                presenter.Descriptor.DisplayName,
                presenter.Descriptor.ProfilePicturePng is null
                    ? null
                    : [.. presenter.Descriptor.ProfilePicturePng]))
            .OrderBy(presenter => presenter.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        session.Receiver.ClientInstanceId,
        session.ProfilePicturePng is null ? null : [.. session.ProfilePicturePng],
        session.ReceiverDisplayName);

    private sealed class SessionRecord(
        string id,
        string pairingCodeHash,
        DateTimeOffset pairingCodeExpiresAt,
        DateTimeOffset expiresAt,
        string sessionSecretHash,
        DisplayDescriptor receiverDisplay,
        string receiverDisplayName,
        string applicationInstanceId,
        byte[]? profilePicturePng,
        Participant receiver,
        int sequenceWindowSize,
        int maximumPresenterConnections)
    {
        internal string Id { get; } = id;

        internal string PairingCodeHash { get; } = pairingCodeHash;

        internal DateTimeOffset PairingCodeExpiresAt { get; } = pairingCodeExpiresAt;

        internal DateTimeOffset ExpiresAt { get; } = expiresAt;

        internal string SessionSecretHash { get; } = sessionSecretHash;

        internal DisplayDescriptor ReceiverDisplay { get; set; } = receiverDisplay;

        internal string ReceiverDisplayName { get; set; } = receiverDisplayName;

        internal string ApplicationInstanceId { get; set; } = applicationInstanceId;

        internal byte[]? ProfilePicturePng { get; set; } = profilePicturePng;

        internal Participant Receiver { get; } = receiver;

        internal int SequenceWindowSize { get; } = sequenceWindowSize;

        internal int MaximumPresenterConnections { get; set; } = maximumPresenterConnections;

        internal bool PairingCodeConsumed { get; set; }

        internal bool IsDiscoverable { get; set; }

        internal PresenterDescriptor? PendingPresenter { get; set; }

        internal Dictionary<string, ConnectedPresenter> Presenters { get; } =
            new(StringComparer.Ordinal);

        internal long PointerCount { get; set; }

        private Dictionary<Guid, string> PointerOrigins { get; } = [];

        private Queue<Guid> PointerOriginOrder { get; } = [];

        internal void RecordPointerOrigin(Guid eventId, string presenterConnectionId)
        {
            PointerOrigins[eventId] = presenterConnectionId;
            PointerOriginOrder.Enqueue(eventId);
            while (PointerOriginOrder.Count > 4_096)
            {
                PointerOrigins.Remove(PointerOriginOrder.Dequeue());
            }
        }

        internal string? TakePointerOrigin(Guid eventId)
        {
            return PointerOrigins.Remove(eventId, out var presenterConnectionId)
                ? presenterConnectionId
                : null;
        }

        internal void RemovePointerOrigins(string presenterConnectionId)
        {
            foreach (var eventId in PointerOrigins
                         .Where(pair => string.Equals(
                             pair.Value,
                             presenterConnectionId,
                             StringComparison.Ordinal))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                PointerOrigins.Remove(eventId);
            }
        }

        internal void ReplacePointerOriginConnection(
            string previousConnectionId,
            string newConnectionId)
        {
            foreach (var eventId in PointerOrigins
                         .Where(pair => string.Equals(
                             pair.Value,
                             previousConnectionId,
                             StringComparison.Ordinal))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                PointerOrigins[eventId] = newConnectionId;
            }
        }

        internal void ClearPointerOrigins()
        {
            PointerOrigins.Clear();
            PointerOriginOrder.Clear();
        }
    }

    private sealed class ConnectedPresenter(
        PresenterDescriptor descriptor,
        Participant participant,
        SequenceNumberTracker sequenceNumbers,
        PointerTokenBucket rateLimiter)
    {
        internal PresenterDescriptor Descriptor { get; set; } = descriptor;

        internal Participant Participant { get; } = participant;

        internal SequenceNumberTracker SequenceNumbers { get; } = sequenceNumbers;

        internal PointerTokenBucket RateLimiter { get; } = rateLimiter;
    }

    private sealed class Participant(
        ClientRole role,
        string clientInstanceId,
        string? connectionId,
        string sessionTokenHash,
        string reconnectTokenHash)
    {
        internal ClientRole Role { get; } = role;

        internal string ClientInstanceId { get; } = clientInstanceId;

        internal string? ConnectionId { get; set; } = connectionId;

        internal string SessionTokenHash { get; } = sessionTokenHash;

        internal string ReconnectTokenHash { get; set; } = reconnectTokenHash;
    }

    private sealed record ConnectionMembership(
        string SessionId,
        ClientRole Role,
        bool Approved);
}
