using Microsoft.Extensions.Options;
using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Validation;
using RemotePointer.Server.RateLimiting;

namespace RemotePointer.Server.Sessions;

public sealed class SessionManager : ISessionManager
{
    private const int DefaultAnnotatorConnections = 2;

    /// <summary>
    /// The group every client shares when it presents no server password. It only ever holds
    /// clients on a relay that allows them, because <see cref="SessionOptions.RequireServerPassword"/>
    /// otherwise rejects an empty key.
    /// </summary>
    public const string OpenGroupKey = "";

    private readonly Dictionary<string, string> connectionGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConnectionMembership> connections = new(StringComparer.Ordinal);
    private readonly object syncRoot = new();
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

    public bool ServerPasswordRequired => sessionOptions.RequireServerPassword;

    /// <summary>
    /// Binds a connection to the group derived from its server password. The relay never sees
    /// the password itself: the client derives a key from it and two clients reach the same
    /// group only by deriving the same key, so groups need no registry and no cleanup.
    /// </summary>
    public RelayGroupChange SetConnectionGroup(string connectionId, string? groupKey)
    {
        EnsureIdentifier(connectionId, nameof(connectionId));
        var normalized = groupKey ?? OpenGroupKey;
        if (normalized.Length > 128)
        {
            throw new SessionOperationException(
                "invalid_group_key",
                "The server password key is not valid.");
        }

        if (normalized.Length == 0 && sessionOptions.RequireServerPassword)
        {
            throw new SessionOperationException(
                "server_password_required",
                "This relay requires a server password. Set one in Settings.");
        }

        lock (syncRoot)
        {
            // A connection that has not presented a password yet sits in the open pool, so
            // that is the group it leaves when it presents one.
            connectionGroups.TryGetValue(connectionId, out var previous);
            previous ??= OpenGroupKey;
            connectionGroups[connectionId] = normalized;
            if (string.Equals(previous, normalized, StringComparison.Ordinal))
            {
                return new RelayGroupChange(normalized, null);
            }

            return new RelayGroupChange(
                normalized,
                previous,
                MoveConnectionSessionNoLock(connectionId, normalized));
        }
    }

    public string GetConnectionGroup(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return OpenGroupKey;
        }

        lock (syncRoot)
        {
            return GetConnectionGroupNoLock(connectionId);
        }
    }

    public CreateSessionResponse CreateHostSession(
        DisplayDescriptor display,
        string connectionId,
        string clientInstanceId,
        string hostDisplayName,
        string? applicationInstanceId = null,
        ClientProfile? profile = null,
        int? maximumAnnotatorConnections = null)
    {
        applicationInstanceId = string.IsNullOrWhiteSpace(applicationInstanceId)
            ? clientInstanceId
            : applicationInstanceId;
        profile ??= new ClientProfile();
        EnsureIdentifier(connectionId, nameof(connectionId));
        EnsureIdentifier(clientInstanceId, nameof(clientInstanceId));
        EnsureIdentifier(applicationInstanceId, nameof(applicationInstanceId));
        EnsureIdentifier(hostDisplayName, nameof(hostDisplayName));
        EnsureValid(ContractValidator.Validate(display), "invalid_display");
        EnsureValid(ContractValidator.Validate(profile), "invalid_profile");

        // Callers that do not choose a limit get the client default, kept within whatever the
        // relay allows, so a relay configured for a single annotator still accepts them.
        var annotatorLimit = maximumAnnotatorConnections
            ?? Math.Min(DefaultAnnotatorConnections, sessionOptions.MaximumAnnotatorsPerHost);
        if (annotatorLimit < 1
            || annotatorLimit > sessionOptions.MaximumAnnotatorsPerHost)
        {
            throw new SessionOperationException(
                "invalid_annotator_limit",
                $"Maximum annotator connections must be between 1 and {sessionOptions.MaximumAnnotatorsPerHost}.");
        }

        lock (syncRoot)
        {
            EnsureConnectionIsUnbound(connectionId);
            EnsureGroupIsPermittedNoLock(connectionId);
            var now = timeProvider.GetUtcNow();
            var sessionId = GenerateUniqueSessionId();
            var sessionSecret = secretGenerator.GenerateSecret();
            var sessionToken = secretGenerator.GenerateSecret();
            var reconnectToken = secretGenerator.GenerateSecret();
            var expiresAt = now.AddHours(sessionOptions.MaximumSessionHours);

            var host = new Participant(
                ClientRole.Host,
                clientInstanceId,
                connectionId,
                secretGenerator.HashSecret(sessionToken),
                secretGenerator.HashSecret(reconnectToken));
            var session = new SessionRecord(
                sessionId,
                now.AddMinutes(sessionOptions.AbandonedSessionLifetimeMinutes),
                expiresAt,
                secretGenerator.HashSecret(sessionSecret),
                display,
                hostDisplayName,
                applicationInstanceId,
                profile.PicturePng is null ? null : [.. profile.PicturePng],
                host,
                sessionOptions.SequenceWindowSize,
                annotatorLimit,
                GetConnectionGroupNoLock(connectionId));
            session.IsDiscoverable = true;

            sessions.Add(sessionId, session);
            connections.Add(
                connectionId,
                new ConnectionMembership(sessionId, ClientRole.Host, Approved: true));

            var credential = new SessionCredential(
                sessionId,
                ClientRole.Host,
                clientInstanceId,
                sessionToken,
                reconnectToken,
                expiresAt);
            return new CreateSessionResponse(sessionId, sessionSecret, credential);
        }
    }

    public IReadOnlyList<AvailableHostDescriptor> GetAvailableHosts(
        string? excludedApplicationInstanceId = null,
        string? connectionId = null)
    {
        lock (syncRoot)
        {
            var groupKey = connectionId is null
                ? OpenGroupKey
                : GetConnectionGroupNoLock(connectionId);
            if (sessionOptions.RequireServerPassword && groupKey.Length == 0)
            {
                return [];
            }

            var now = timeProvider.GetUtcNow();
            return sessions.Values
                .Where(session =>
                    session.IsDiscoverable
                    && string.Equals(session.GroupKey, groupKey, StringComparison.Ordinal)
                    && session.ExpiresAt > now
                    && session.Host.ConnectionId is not null
                    && session.PendingAnnotator is null
                    && session.Annotators.Count < session.MaximumAnnotatorConnections
                    && !string.Equals(
                        session.ApplicationInstanceId,
                        excludedApplicationInstanceId,
                        StringComparison.Ordinal))
                .OrderBy(session => session.HostDisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(session => new AvailableHostDescriptor(
                    session.Id,
                    session.HostDisplayName,
                    session.ApplicationInstanceId,
                    session.ProfilePicturePng is null
                        ? null
                        : [.. session.ProfilePicturePng]))
                .ToArray();
        }
    }

    public bool SetHostDiscoverable(
        string sessionId,
        string hostConnectionId,
        bool discoverable)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(hostConnectionId, nameof(hostConnectionId));

        lock (syncRoot)
        {
            var session = GetActiveSession(sessionId);
            EnsureMembership(
                hostConnectionId,
                sessionId,
                ClientRole.Host,
                requireApproved: true);
            session.IsDiscoverable = discoverable;
            return session.IsDiscoverable;
        }
    }

    public JoinSessionResult RequestToJoinHost(
        DirectJoinRequest request,
        string connectionId,
        string displayName,
        string? applicationInstanceId = null)
    {
        EnsureIdentifier(connectionId, nameof(connectionId));
        EnsureIdentifier(displayName, nameof(displayName));
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
                || session.Host.ConnectionId is null)
            {
                return RejectedJoin("The selected host is no longer available.");
            }

            return BindPendingAnnotatorNoLock(
                session,
                connectionId,
                request.ClientInstanceId,
                applicationInstanceId,
                displayName,
                request.ClientVersion,
                request.Profile?.PicturePng);
        }
    }

    public HostDisplayUpdateResult UpdateHostDisplay(
        string sessionId,
        string hostConnectionId,
        DisplayDescriptor display)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(hostConnectionId, nameof(hostConnectionId));
        EnsureValid(ContractValidator.Validate(display), "invalid_display");

        lock (syncRoot)
        {
            var session = GetActiveSession(sessionId);
            EnsureMembership(
                hostConnectionId,
                sessionId,
                ClientRole.Host,
                requireApproved: true);
            session.HostDisplay = display;
            return new HostDisplayUpdateResult(
                session.Id,
                session.Annotators.Values
                    .Select(annotator => annotator.Participant.ConnectionId)
                    .OfType<string>()
                    .ToArray(),
                display);
        }
    }

    public HostClientSettingsUpdateResult UpdateHostClientSettings(
        string sessionId,
        string hostConnectionId,
        string hostDisplayName,
        ClientProfile profile,
        int maximumAnnotatorConnections)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(hostConnectionId, nameof(hostConnectionId));
        EnsureIdentifier(hostDisplayName, nameof(hostDisplayName));
        EnsureValid(ContractValidator.Validate(profile), "invalid_profile");
        if (maximumAnnotatorConnections < 1
            || maximumAnnotatorConnections > sessionOptions.MaximumAnnotatorsPerHost)
        {
            throw new SessionOperationException(
                "invalid_annotator_limit",
                $"Maximum annotator connections must be between 1 and {sessionOptions.MaximumAnnotatorsPerHost}.");
        }

        lock (syncRoot)
        {
            var session = GetActiveSession(sessionId);
            EnsureMembership(
                hostConnectionId,
                sessionId,
                ClientRole.Host,
                requireApproved: true);
            session.HostDisplayName = hostDisplayName;
            session.ProfilePicturePng = profile.PicturePng is null
                ? null
                : [.. profile.PicturePng];
            session.MaximumAnnotatorConnections = maximumAnnotatorConnections;
            return new HostClientSettingsUpdateResult(
                hostConnectionId,
                session.Annotators.Values
                    .Select(annotator => annotator.Participant.ConnectionId)
                    .OfType<string>()
                    .ToArray(),
                CreateState(session));
        }
    }

    public ApproveAnnotatorResult ApproveAnnotator(
        string sessionId,
        string annotatorConnectionId,
        string hostConnectionId)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(annotatorConnectionId, nameof(annotatorConnectionId));
        EnsureIdentifier(hostConnectionId, nameof(hostConnectionId));

        lock (syncRoot)
        {
            var session = GetActiveSession(sessionId);
            EnsureMembership(
                hostConnectionId,
                sessionId,
                ClientRole.Host,
                requireApproved: true);

            var pending = session.PendingAnnotator;
            if (pending is null
                || !string.Equals(
                    pending.ConnectionId,
                    annotatorConnectionId,
                    StringComparison.Ordinal))
            {
                throw new SessionOperationException(
                    "annotator_not_pending",
                    "The selected annotator no longer has a pending request.");
            }

            var sessionToken = secretGenerator.GenerateSecret();
            var reconnectToken = secretGenerator.GenerateSecret();
            var participant = new Participant(
                ClientRole.Annotator,
                pending.ClientInstanceId,
                annotatorConnectionId,
                secretGenerator.HashSecret(sessionToken),
                secretGenerator.HashSecret(reconnectToken));
            session.Annotators.Add(
                annotatorConnectionId,
                new ConnectedAnnotator(
                    pending,
                    participant,
                    new SequenceNumberTracker(session.SequenceWindowSize),
                    new PointerTokenBucket(
                        rateLimitOptions.EventsPerSecond,
                        rateLimitOptions.BurstSize,
                        timeProvider.GetUtcNow())));
            session.PendingAnnotator = null;
            connections[annotatorConnectionId] = new ConnectionMembership(
                session.Id,
                ClientRole.Annotator,
                Approved: true);

            var credential = new SessionCredential(
                session.Id,
                ClientRole.Annotator,
                pending.ClientInstanceId,
                sessionToken,
                reconnectToken,
                session.ExpiresAt);
            var state = CreateState(session);
            return new ApproveAnnotatorResult(
                session.Id,
                annotatorConnectionId,
                hostConnectionId,
                credential,
                state);
        }
    }

    public RejectAnnotatorResult RejectAnnotator(
        string sessionId,
        string annotatorConnectionId,
        string hostConnectionId)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(annotatorConnectionId, nameof(annotatorConnectionId));
        EnsureIdentifier(hostConnectionId, nameof(hostConnectionId));

        lock (syncRoot)
        {
            var session = GetActiveSession(sessionId);
            EnsureMembership(
                hostConnectionId,
                sessionId,
                ClientRole.Host,
                requireApproved: true);
            var pending = session.PendingAnnotator;
            if (pending is null
                || !string.Equals(
                    pending.ConnectionId,
                    annotatorConnectionId,
                    StringComparison.Ordinal))
            {
                throw new SessionOperationException(
                    "annotator_not_pending",
                    "The selected annotator no longer has a pending request.");
            }

            session.PendingAnnotator = null;
            connections.Remove(annotatorConnectionId);
            return new RejectAnnotatorResult(
                sessionId,
                annotatorConnectionId,
                hostConnectionId);
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
            if (membership.Role != ClientRole.Annotator || !membership.Approved)
            {
                throw new SessionOperationException(
                    "annotator_required",
                    "Only the approved annotator can send pointer events.");
            }

            if (!string.Equals(membership.SessionId, pointerEvent.SessionId, StringComparison.Ordinal))
            {
                throw new SessionOperationException(
                    "session_mismatch",
                    "The pointer event does not belong to this connection's session.");
            }

            var session = GetActiveSession(membership.SessionId);
            if (!session.Annotators.TryGetValue(connectionId, out var annotator))
            {
                throw new SessionOperationException(
                    "annotator_not_connected",
                    "The annotator is no longer connected to this session.");
            }
            // The budget is per annotator: each one sends its own gesture stream at the
            // client's update rate, so a shared session budget would throttle everybody as
            // soon as two annotators drew at the same time.
            if (!annotator.RateLimiter.TryAcquire(now))
            {
                throw new SessionOperationException(
                    "pointer_rate_exceeded",
                    "The pointer event rate limit was exceeded.");
            }

            if (!annotator.SequenceNumbers.TryAccept(pointerEvent.SequenceNumber))
            {
                return new PointerRelayResult(
                    PointerRelayDisposition.IgnoredSequence,
                    session.Id,
                    session.Host.ConnectionId);
            }

            session.PointerCount++;
            session.RecordPointerOrigin(pointerEvent.EventId, connectionId);
            return new PointerRelayResult(
                PointerRelayDisposition.Accepted,
                session.Id,
                session.Host.ConnectionId);
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
            if (membership.Role != ClientRole.Host || !membership.Approved)
            {
                throw new SessionOperationException(
                    "host_required",
                    "Only the session host can acknowledge pointer events.");
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
            ConnectedAnnotator? connectedAnnotator = null;
            Participant? participant;
            if (request.Role == ClientRole.Host)
            {
                participant = session.Host;
            }
            else if (request.Role == ClientRole.Annotator)
            {
                connectedAnnotator = session.Annotators.Values.FirstOrDefault(candidate =>
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
                participant = connectedAnnotator?.Participant;
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
            if (connectedAnnotator is not null)
            {
                var previousAnnotatorKey = session.Annotators
                    .First(pair => ReferenceEquals(pair.Value, connectedAnnotator))
                    .Key;
                if (!string.Equals(previousAnnotatorKey, connectionId, StringComparison.Ordinal))
                {
                    session.Annotators.Remove(previousAnnotatorKey);
                }

                session.ReplacePointerOriginConnection(previousAnnotatorKey, connectionId);
                connectedAnnotator.Descriptor = connectedAnnotator.Descriptor with
                {
                    ConnectionId = connectionId,
                };
                session.Annotators[connectionId] = connectedAnnotator;
            }
            if (participant.Role == ClientRole.Host)
            {
                if (!string.IsNullOrWhiteSpace(applicationInstanceId))
                {
                    EnsureIdentifier(applicationInstanceId, nameof(applicationInstanceId));
                    session.ApplicationInstanceId = applicationInstanceId;
                }

                // The resuming connection presented its password before it resumed, and a
                // client whose password changed while it was away must not come back into
                // the group it left.
                session.GroupKey = GetConnectionGroupNoLock(connectionId);
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
                || (!membership.Approved && membership.Role != ClientRole.Annotator))
            {
                throw new SessionOperationException(
                    "session_member_required",
                    "Only a session member can end the session.");
            }

            var session = GetActiveSession(sessionId);
            if (membership.Role == ClientRole.Annotator)
            {
                // An annotator waiting for approval is still bound to the session, so it can
                // withdraw its own request without waiting for the host to answer.
                return membership.Approved
                    ? DisconnectAnnotatorNoLock(session, connectionId)
                    : CancelPendingAnnotatorNoLock(session, connectionId);
            }

            return TerminateSessionNoLock(session);
        }
    }

    public SessionTerminationResult DisconnectAnnotators(
        string sessionId,
        string hostConnectionId)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(hostConnectionId, nameof(hostConnectionId));

        lock (syncRoot)
        {
            var session = GetActiveSession(sessionId);
            EnsureMembership(
                hostConnectionId,
                sessionId,
                ClientRole.Host,
                requireApproved: true);
            if (session.Annotators.Count == 0 && session.PendingAnnotator is null)
            {
                throw new SessionOperationException(
                    "annotator_not_connected",
                    "No annotator is connected to this host.");
            }

            return DisconnectAnnotatorsNoLock(session);
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

                if (!session.AbandonmentResolved && session.AbandonedAfter <= now)
                {
                    session.AbandonmentResolved = true;
                    if (IsAbandonedNoLock(session))
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
            // Group membership belongs to the connection rather than to a session, so it is
            // released here whether or not the connection ever joined one.
            connectionGroups.Remove(connectionId);
            if (!connections.TryGetValue(connectionId, out var membership)
                || !sessions.TryGetValue(membership.SessionId, out var session))
            {
                return null;
            }

            if (membership.Role == ClientRole.Host)
            {
                connections.Remove(connectionId);
                session.Host.ConnectionId = null;

                var annotatorConnectionIds = session.Annotators.Values
                    .Select(annotator => annotator.Participant.ConnectionId)
                    .OfType<string>()
                    .Concat(session.PendingAnnotator is null
                        ? []
                        : [session.PendingAnnotator.ConnectionId])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                foreach (var annotatorConnectionId in annotatorConnectionIds)
                {
                    connections.Remove(annotatorConnectionId);
                }

                session.Annotators.Clear();
                session.PendingAnnotator = null;
                session.ClearPointerOrigins();
                return new ConnectionDisconnectResult(
                    session.Id,
                    ClientRole.Host,
                    annotatorConnectionIds,
                    HostConnectionId: null,
                    CreateState(session),
                    session.GroupKey);
            }

            connections.Remove(connectionId);
            var cancelledAnnotatorRequestConnectionId = membership.Approved
                ? null
                : connectionId;
            if (!membership.Approved)
            {
                if (session.PendingAnnotator?.ConnectionId == connectionId)
                {
                    session.PendingAnnotator = null;
                }
            }
            else
            {
                session.Annotators.Remove(connectionId);
                session.RemovePointerOrigins(connectionId);
            }

            return new ConnectionDisconnectResult(
                session.Id,
                ClientRole.Annotator,
                AnnotatorConnectionIdsToEnd: [],
                session.Host.ConnectionId,
                CreateState(session),
                session.GroupKey,
                cancelledAnnotatorRequestConnectionId);
        }
    }

    private static void ValidateOptions(
        SessionOptions sessionOptions,
        PointerRateLimitOptions rateLimitOptions)
    {
        if (sessionOptions.AbandonedSessionLifetimeMinutes <= 0
            || sessionOptions.MaximumSessionHours <= 0
            || sessionOptions.MaximumSessionHours > 8
            || sessionOptions.SequenceWindowSize <= 0
            || sessionOptions.MaximumAnnotatorsPerHost is < 1
                or > ContractValidator.MaximumConnectedAnnotators
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
            // A rejected identifier is invalid input like any other contract failure, so it
            // carries a stable audit code instead of surfacing as an unexpected hub error.
            throw new SessionOperationException(
                "invalid_identifier",
                $"{parameterName} must be a non-empty identifier of at most 128 characters.");
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

    private JoinSessionResult BindPendingAnnotatorNoLock(
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
        // Sharing a server password is what makes two clients visible and reachable to each
        // other, so a request that crosses that boundary is refused without confirming whether
        // the session exists.
        if (!string.Equals(
                session.GroupKey,
                GetConnectionGroupNoLock(connectionId),
                StringComparison.Ordinal))
        {
            return RejectedJoin("The selected host is no longer available.");
        }

        if (string.Equals(
                session.ApplicationInstanceId,
                applicationInstanceId,
                StringComparison.Ordinal))
        {
            return RejectedJoin("A client cannot connect to itself.");
        }

        if (session.Annotators.Count >= session.MaximumAnnotatorConnections)
        {
            return RejectedJoin("The host has reached its connection limit.");
        }

        if (session.PendingAnnotator is not null)
        {
            return RejectedJoin("The session already has an annotator request.");
        }

        // An access request settles the question the grace period asks, so the session is no
        // longer a candidate for collection even on a relay that publishes nothing.
        session.AbandonmentResolved = true;
        var annotator = new AnnotatorDescriptor(
            connectionId,
            clientInstanceId,
            displayName,
            clientVersion,
            profilePicturePng is null ? null : [.. profilePicturePng]);
        session.PendingAnnotator = annotator;
        connections.Add(
            connectionId,
            new ConnectionMembership(session.Id, ClientRole.Annotator, Approved: false));

        return new JoinSessionResult(
            new JoinResponse(true, session.Id, null),
            session.Host.ConnectionId,
            annotator);
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

    private string GetConnectionGroupNoLock(string connectionId) =>
        connectionGroups.TryGetValue(connectionId, out var groupKey) ? groupKey : OpenGroupKey;

    /// <summary>
    /// Carries a session across with the host that changed its server password. A session
    /// keeps the key it was published under otherwise, which would leave the host listed
    /// and joinable under the password it just left. A pending request that no longer shares
    /// the session's group is cancelled from either side: approving one would form a session
    /// across the boundary the password draws. An already approved annotator keeps its place,
    /// because the host admitted it by name and ends it with Disconnect all annotators.
    /// </summary>
    private SessionTerminationResult? MoveConnectionSessionNoLock(
        string connectionId,
        string groupKey)
    {
        if (!connections.TryGetValue(connectionId, out var membership)
            || !sessions.TryGetValue(membership.SessionId, out var session))
        {
            return null;
        }

        if (membership.Role == ClientRole.Host)
        {
            session.GroupKey = groupKey;
        }
        else if (membership.Approved)
        {
            return null;
        }

        var pending = session.PendingAnnotator;
        if (pending is null
            || string.Equals(
                session.GroupKey,
                GetConnectionGroupNoLock(pending.ConnectionId),
                StringComparison.Ordinal))
        {
            return null;
        }

        return CancelPendingAnnotatorNoLock(session, pending.ConnectionId);
    }

    /// <summary>
    /// Rejects a client that has not presented a server password on a relay that requires one,
    /// so an unidentified connection can neither publish itself nor see or reach anyone else.
    /// </summary>
    private void EnsureGroupIsPermittedNoLock(string connectionId)
    {
        if (sessionOptions.RequireServerPassword
            && GetConnectionGroupNoLock(connectionId).Length == 0)
        {
            throw new SessionOperationException(
                "server_password_required",
                "This relay requires a server password. Set one in Settings.");
        }
    }

    /// <summary>
    /// A session nobody has asked to join is abandoned only when nothing can make it reachable.
    /// A connected host that chose to be invisible can publish itself at any time, so it
    /// keeps its session; a disconnected shell that is also hidden can no longer be joined and
    /// is collected.
    /// </summary>
    private static bool IsAbandonedNoLock(SessionRecord session) =>
        !session.IsDiscoverable && session.Host.ConnectionId is null;

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
        var connectionIds = connections
            .Where(pair => string.Equals(pair.Value.SessionId, session.Id, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var connectionId in connectionIds)
        {
            connections.Remove(connectionId);
        }

        return new SessionTerminationResult(
            session.Id,
            connectionIds,
            session.PointerCount,
            session.GroupKey);
    }

    private SessionTerminationResult DisconnectAnnotatorsNoLock(SessionRecord session)
    {
        var annotatorConnectionIds = session.Annotators.Values
            .Select(annotator => annotator.Participant.ConnectionId)
            .OfType<string>()
            .Concat(session.PendingAnnotator is null
                ? []
                : [session.PendingAnnotator.ConnectionId])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var annotatorConnectionId in annotatorConnectionIds)
        {
            connections.Remove(annotatorConnectionId);
        }

        session.Annotators.Clear();
        session.PendingAnnotator = null;
        session.ClearPointerOrigins();
        return new SessionTerminationResult(
            session.Id,
            annotatorConnectionIds,
            session.PointerCount,
            session.GroupKey,
            HostPreserved: true,
            AnnotatorConnectionId: annotatorConnectionIds.FirstOrDefault(),
            HostConnectionId: session.Host.ConnectionId,
            State: CreateState(session),
            AnnotatorConnectionIds: annotatorConnectionIds);
    }

    private SessionTerminationResult CancelPendingAnnotatorNoLock(
        SessionRecord session,
        string annotatorConnectionId)
    {
        if (session.PendingAnnotator is null
            || !string.Equals(
                session.PendingAnnotator.ConnectionId,
                annotatorConnectionId,
                StringComparison.Ordinal))
        {
            throw new SessionOperationException(
                "annotator_not_pending",
                "This connection no longer has a pending request.");
        }

        connections.Remove(annotatorConnectionId);
        session.PendingAnnotator = null;
        return new SessionTerminationResult(
            session.Id,
            [annotatorConnectionId],
            session.PointerCount,
            session.GroupKey,
            HostPreserved: true,
            AnnotatorConnectionId: annotatorConnectionId,
            HostConnectionId: session.Host.ConnectionId,
            State: CreateState(session),
            AnnotatorConnectionIds: [annotatorConnectionId],
            CancelledAnnotatorRequestConnectionId: annotatorConnectionId);
    }

    private SessionTerminationResult DisconnectAnnotatorNoLock(
        SessionRecord session,
        string annotatorConnectionId)
    {
        connections.Remove(annotatorConnectionId);
        session.Annotators.Remove(annotatorConnectionId);
        session.RemovePointerOrigins(annotatorConnectionId);
        return new SessionTerminationResult(
            session.Id,
            [annotatorConnectionId],
            session.PointerCount,
            session.GroupKey,
            HostPreserved: true,
            AnnotatorConnectionId: annotatorConnectionId,
            HostConnectionId: session.Host.ConnectionId,
            State: CreateState(session),
            AnnotatorConnectionIds: [annotatorConnectionId]);
    }

    private static SessionStateMessage CreateState(SessionRecord session) => new(
        session.Id,
        Approved: session.Annotators.Count > 0,
        session.HostDisplay,
        session.ExpiresAt,
        session.IsDiscoverable,
        session.Annotators.Values
            .Select(annotator => new ConnectedAnnotatorDescriptor(
                annotator.Descriptor.DisplayName,
                annotator.Descriptor.ProfilePicturePng is null
                    ? null
                    : [.. annotator.Descriptor.ProfilePicturePng]))
            .OrderBy(annotator => annotator.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        session.Host.ClientInstanceId,
        session.ProfilePicturePng is null ? null : [.. session.ProfilePicturePng],
        session.HostDisplayName);

    private sealed class SessionRecord(
        string id,
        DateTimeOffset abandonedAfter,
        DateTimeOffset expiresAt,
        string sessionSecretHash,
        DisplayDescriptor hostDisplay,
        string hostDisplayName,
        string applicationInstanceId,
        byte[]? profilePicturePng,
        Participant host,
        int sequenceWindowSize,
        int maximumAnnotatorConnections,
        string groupKey)
    {
        internal string Id { get; } = id;

        internal DateTimeOffset AbandonedAfter { get; } = abandonedAfter;

        internal DateTimeOffset ExpiresAt { get; } = expiresAt;

        internal string SessionSecretHash { get; } = sessionSecretHash;

        internal DisplayDescriptor HostDisplay { get; set; } = hostDisplay;

        internal string HostDisplayName { get; set; } = hostDisplayName;

        internal string ApplicationInstanceId { get; set; } = applicationInstanceId;

        internal byte[]? ProfilePicturePng { get; set; } = profilePicturePng;

        internal Participant Host { get; } = host;

        internal int SequenceWindowSize { get; } = sequenceWindowSize;

        internal int MaximumAnnotatorConnections { get; set; } = maximumAnnotatorConnections;

        internal string GroupKey { get; set; } = groupKey;

        internal bool AbandonmentResolved { get; set; }

        internal bool IsDiscoverable { get; set; }

        internal AnnotatorDescriptor? PendingAnnotator { get; set; }

        internal Dictionary<string, ConnectedAnnotator> Annotators { get; } =
            new(StringComparer.Ordinal);

        internal long PointerCount { get; set; }

        private Dictionary<Guid, string> PointerOrigins { get; } = [];

        private Queue<Guid> PointerOriginOrder { get; } = [];

        internal void RecordPointerOrigin(Guid eventId, string annotatorConnectionId)
        {
            PointerOrigins[eventId] = annotatorConnectionId;
            PointerOriginOrder.Enqueue(eventId);
            while (PointerOriginOrder.Count > 4_096)
            {
                PointerOrigins.Remove(PointerOriginOrder.Dequeue());
            }
        }

        internal string? TakePointerOrigin(Guid eventId)
        {
            return PointerOrigins.Remove(eventId, out var annotatorConnectionId)
                ? annotatorConnectionId
                : null;
        }

        internal void RemovePointerOrigins(string annotatorConnectionId)
        {
            foreach (var eventId in PointerOrigins
                         .Where(pair => string.Equals(
                             pair.Value,
                             annotatorConnectionId,
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

    private sealed class ConnectedAnnotator(
        AnnotatorDescriptor descriptor,
        Participant participant,
        SequenceNumberTracker sequenceNumbers,
        PointerTokenBucket rateLimiter)
    {
        internal AnnotatorDescriptor Descriptor { get; set; } = descriptor;

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
