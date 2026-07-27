using Microsoft.Extensions.Options;
using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Validation;
using RemotePointer.Server.RateLimiting;

namespace RemotePointer.Server.Sessions;

public sealed class SessionManager : ISessionManager
{
    private const int DefaultAnnotatorConnections = 2;

    private readonly Dictionary<string, string> connectionRooms = new(StringComparer.Ordinal);
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

    /// <summary>
    /// Puts a connection in the room it named. Rooms are plain names rather than secrets — every
    /// connection that gets this far already presented the server password — so a room needs no
    /// registry and no cleanup: an unused one simply has no members.
    /// </summary>
    public RelayRoomChange SetConnectionRoom(string connectionId, string? room)
    {
        EnsureIdentifier(connectionId, nameof(connectionId));

        // Normalised rather than rejected: the name is a label, and a client that sends an
        // unusable one belongs in the default room instead of being refused a directory.
        var normalized = RoomName.Normalize(room);

        lock (syncRoot)
        {
            // A connection that has not named a room yet is in the default one, so that is the
            // room it leaves when it names another.
            connectionRooms.TryGetValue(connectionId, out var previous);
            previous ??= RoomName.Default;
            connectionRooms[connectionId] = normalized;
            if (string.Equals(previous, normalized, StringComparison.Ordinal))
            {
                return new RelayRoomChange(normalized, null);
            }

            return new RelayRoomChange(
                normalized,
                previous,
                MoveConnectionSessionNoLock(connectionId, normalized));
        }
    }

    public string GetConnectionRoom(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return RoomName.Default;
        }

        lock (syncRoot)
        {
            return GetConnectionRoomNoLock(connectionId);
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
                GetConnectionRoomNoLock(connectionId));
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
            var room = connectionId is null
                ? RoomName.Default
                : GetConnectionRoomNoLock(connectionId);
            var now = timeProvider.GetUtcNow();
            return sessions.Values
                .Where(session =>
                    session.IsDiscoverable
                    && string.Equals(session.Room, room, StringComparison.Ordinal)
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
                        timeProvider.GetUtcNow()))
                {
                    JoinSequence = session.NextJoinSequence++,
                });
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

    public IReadOnlyList<AnnotationColorAssignment> SetAnnotationColorPreference(
        string connectionId,
        string? preferredColor)
    {
        EnsureIdentifier(connectionId, nameof(connectionId));

        lock (syncRoot)
        {
            var membership = GetMembership(connectionId);
            if (membership.Role != ClientRole.Annotator || !membership.Approved)
            {
                throw new SessionOperationException(
                    "annotator_required",
                    "Only an approved annotator can choose a drawing colour.");
            }

            var session = GetActiveSession(membership.SessionId);
            if (!session.Annotators.TryGetValue(connectionId, out var annotator))
            {
                throw new SessionOperationException(
                    "annotator_not_connected",
                    "The annotator is no longer connected to this session.");
            }

            annotator.PreferredAnnotationColor = AnnotationColors.Normalize(preferredColor);
            var changes = AllocateAnnotationColors(session);

            // The caller is always answered, even when allocation left it where it was. It
            // applied its own pick the moment the user made it, so silence here would leave it
            // drawing in a colour the relay never granted — the exact disagreement between the
            // two screens that allocating centrally is meant to prevent.
            return changes.Any(change => string.Equals(
                    change.ConnectionId,
                    connectionId,
                    StringComparison.Ordinal))
                ? changes
                : [.. changes, new AnnotationColorAssignment(
                    connectionId,
                    annotator.AssignedAnnotationColor)];
        }
    }

    public IReadOnlyList<AnnotationColorAssignment> RefreshAnnotationColors(string sessionId)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));

        lock (syncRoot)
        {
            // Deliberately tolerant. Callers run this after anything that could have changed the
            // membership, including departures that took the whole session with them.
            return sessions.TryGetValue(sessionId, out var session)
                ? AllocateAnnotationColors(session)
                : [];
        }
    }

    /// <summary>
    /// Reallocates the whole session from its annotators' preferences and reports what moved.
    /// Reallocating everything rather than patching the one that changed is what makes a
    /// displaced annotator drop back onto its preference once the holder leaves.
    /// </summary>
    private static IReadOnlyList<AnnotationColorAssignment> AllocateAnnotationColors(
        SessionRecord session)
    {
        var annotators = session.Annotators
            .OrderBy(pair => pair.Value.JoinSequence)
            .ToArray();
        var allocated = AnnotationColorAllocator.Allocate(
            [.. annotators.Select(pair => pair.Value.PreferredAnnotationColor)]);

        var changes = new List<AnnotationColorAssignment>();
        for (var index = 0; index < annotators.Length; index++)
        {
            var annotator = annotators[index].Value;
            if (string.Equals(
                    annotator.AssignedAnnotationColor,
                    allocated[index],
                    StringComparison.Ordinal))
            {
                continue;
            }

            annotator.AssignedAnnotationColor = allocated[index];
            changes.Add(new AnnotationColorAssignment(annotators[index].Key, allocated[index]));
        }

        return changes;
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

            if (annotator.IsPaused)
            {
                return new PointerRelayResult(
                    PointerRelayDisposition.Paused,
                    session.Id,
                    session.Host.ConnectionId,
                    annotator.Participant.ClientInstanceId);
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
                    session.Host.ConnectionId,
                    annotator.Participant.ClientInstanceId);
            }

            session.PointerCount++;
            session.RecordPointerOrigin(pointerEvent.EventId, connectionId);
            return new PointerRelayResult(
                PointerRelayDisposition.Accepted,
                session.Id,
                session.Host.ConnectionId,
                annotator.Participant.ClientInstanceId);
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

                // The resuming connection named its room before it resumed, and a client that
                // changed rooms while it was away must not bring its session back into the
                // room it left.
                session.Room = GetConnectionRoomNoLock(connectionId);
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

    public SessionTerminationResult DisconnectAnnotator(
        string sessionId,
        string hostConnectionId,
        string annotatorId)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(hostConnectionId, nameof(hostConnectionId));
        EnsureIdentifier(annotatorId, nameof(annotatorId));

        lock (syncRoot)
        {
            var session = GetActiveSession(sessionId);
            EnsureMembership(
                hostConnectionId,
                sessionId,
                ClientRole.Host,
                requireApproved: true);
            var annotatorConnectionId = FindAnnotatorConnectionIdNoLock(session, annotatorId)
                ?? throw new SessionOperationException(
                    "annotator_not_connected",
                    "That annotator is no longer connected to this host.");
            return DisconnectAnnotatorNoLock(session, annotatorConnectionId);
        }
    }

    /// <summary>
    /// A paused annotator keeps its session, its credential, and its place in the host's list;
    /// only its pointer events stop being relayed. That is what separates pausing from
    /// disconnecting: the host can lift it again without the annotator asking to join twice.
    /// </summary>
    public AnnotatorPauseResult SetAnnotatorPaused(
        string sessionId,
        string hostConnectionId,
        string? annotatorId,
        bool paused)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureIdentifier(hostConnectionId, nameof(hostConnectionId));
        if (annotatorId is not null)
        {
            EnsureIdentifier(annotatorId, nameof(annotatorId));
        }

        lock (syncRoot)
        {
            var session = GetActiveSession(sessionId);
            EnsureMembership(
                hostConnectionId,
                sessionId,
                ClientRole.Host,
                requireApproved: true);
            var affected = annotatorId is null
                ? session.Annotators.Values.ToArray()
                : session.Annotators.Values
                    .Where(annotator => string.Equals(
                        annotator.Participant.ClientInstanceId,
                        annotatorId,
                        StringComparison.Ordinal))
                    .ToArray();
            if (affected.Length == 0)
            {
                throw new SessionOperationException(
                    "annotator_not_connected",
                    "No matching annotator is connected to this host.");
            }

            var notify = new List<string>();
            foreach (var annotator in affected)
            {
                if (annotator.IsPaused != paused && annotator.Participant.ConnectionId is { } id)
                {
                    notify.Add(id);
                }

                annotator.IsPaused = paused;
            }

            return new AnnotatorPauseResult(
                session.Id,
                notify,
                session.Host.ConnectionId,
                CreateState(session),
                paused);
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
            connectionRooms.Remove(connectionId);
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
                    session.Room);
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
                session.Room,
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
        // Sharing a room is what makes two clients visible and reachable to each other, so a
        // request from outside the session's room is refused without confirming whether the
        // session exists.
        if (!string.Equals(
                session.Room,
                GetConnectionRoomNoLock(connectionId),
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

    private string GetConnectionRoomNoLock(string connectionId) =>
        connectionRooms.TryGetValue(connectionId, out var room) ? room : RoomName.Default;

    /// <summary>
    /// Carries a session across with the host that changed rooms. A session keeps the room it
    /// was published in otherwise, which would leave the host listed and joinable in the room
    /// it just left. A pending request that no longer shares the session's room is cancelled
    /// from either side: approving one would form a session across two rooms. An already
    /// approved annotator keeps its place, because the host admitted it by name and ends it
    /// from its connected-annotator list.
    /// </summary>
    private SessionTerminationResult? MoveConnectionSessionNoLock(
        string connectionId,
        string room)
    {
        if (!connections.TryGetValue(connectionId, out var membership)
            || !sessions.TryGetValue(membership.SessionId, out var session))
        {
            return null;
        }

        if (membership.Role == ClientRole.Host)
        {
            session.Room = room;
        }
        else if (membership.Approved)
        {
            return null;
        }

        var pending = session.PendingAnnotator;
        if (pending is null
            || string.Equals(
                session.Room,
                GetConnectionRoomNoLock(pending.ConnectionId),
                StringComparison.Ordinal))
        {
            return null;
        }

        return CancelPendingAnnotatorNoLock(session, pending.ConnectionId);
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
            session.Room);
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
            session.Room,
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
            session.Room,
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
            session.Room,
            HostPreserved: true,
            AnnotatorConnectionId: annotatorConnectionId,
            HostConnectionId: session.Host.ConnectionId,
            State: CreateState(session),
            AnnotatorConnectionIds: [annotatorConnectionId]);
    }

    private static string? FindAnnotatorConnectionIdNoLock(
        SessionRecord session,
        string annotatorId) =>
        session.Annotators.Values
            .FirstOrDefault(annotator => string.Equals(
                annotator.Participant.ClientInstanceId,
                annotatorId,
                StringComparison.Ordinal))
            ?.Participant.ConnectionId;

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
                    : [.. annotator.Descriptor.ProfilePicturePng],
                annotator.Participant.ClientInstanceId,
                annotator.IsPaused))
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
        string room)
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

        internal string Room { get; set; } = room;

        internal bool AbandonmentResolved { get; set; }

        internal bool IsDiscoverable { get; set; }

        internal AnnotatorDescriptor? PendingAnnotator { get; set; }

        internal Dictionary<string, ConnectedAnnotator> Annotators { get; } =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Stamped onto each admitted annotator so colour allocation has a stable oldest-first
        /// order. The dictionary above is keyed by connection id, which a reconnect changes.
        /// </summary>
        internal long NextJoinSequence { get; set; }

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

        /// <summary>
        /// Set by the host, and carried across a reconnect because the annotator record outlives
        /// the connection: resuming must not hand back the drawing rights the host took away.
        /// </summary>
        internal bool IsPaused { get; set; }

        /// <summary>
        /// Fixes this annotator's place in the colour queue. Allocation runs oldest first, so an
        /// annotator already drawing keeps its colour when a later one wants the same.
        /// </summary>
        internal long JoinSequence { get; init; }

        /// <summary>The colour this annotator asked for, which it may not be able to have.</summary>
        internal string? PreferredAnnotationColor { get; set; }

        /// <summary>The colour it was actually given, and the one it draws in.</summary>
        internal string AssignedAnnotationColor { get; set; } = AnnotationColors.Default;
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
