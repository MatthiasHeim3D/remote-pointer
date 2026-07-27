using Microsoft.Extensions.Options;
using RemoteAnnotate.Contracts.Messages;
using RemoteAnnotate.Server.RateLimiting;
using RemoteAnnotate.Server.Sessions;

namespace RemoteAnnotate.Server.Tests.Sessions;

public sealed class SessionManagerTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Host_IsAutomaticallyDiscoverableAndDirectJoinStillRequiresApproval()
    {
        var context = CreateContext();
        var created = CreateHost(context);

        var available = Assert.Single(context.Manager.GetAvailableHosts());
        Assert.Equal(created.SessionId, available.SessionId);
        Assert.Equal("Host Machine", available.DisplayName);

        var join = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "annotator-client", "1.0.0"),
            "annotator-connection",
            "Annotator Machine");

        Assert.True(join.Response.Accepted);
        Assert.NotNull(join.Annotator);
        Assert.Empty(context.Manager.GetAvailableHosts());
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.AcceptPointer(
                "annotator-connection",
                CreatePointer(InitialTime, created.SessionId, 0)));
    }

    [Fact]
    public void AnnotatorProfilePicture_IsIncludedInApprovalAndConnectedState()
    {
        var context = CreateContext();
        var created = CreateHost(context);
        byte[] picture = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        var join = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(
                created.SessionId,
                "annotator-client",
                "1.0.0",
                new ClientProfile(picture)),
            "annotator-connection",
            "Annotator Machine");
        var approval = context.Manager.ApproveAnnotator(
            created.SessionId,
            join.Annotator!.ConnectionId,
            "host-connection");

        Assert.Equal(picture, join.Annotator.ProfilePicturePng);
        var connectedAnnotator = Assert.Single(approval.State.ConnectedAnnotators!);
        Assert.Equal("Annotator Machine", connectedAnnotator.DisplayName);
        Assert.Equal(picture, connectedAnnotator.ProfilePicturePng);
    }

    [Fact]
    public void Discovery_ExcludesOnlySameApplicationInstanceAndIncludesProfilePicture()
    {
        var context = CreateContext();
        byte[] picture = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var created = context.Manager.CreateHostSession(
            CreateDisplay(),
            "host-connection",
            "shared-machine-profile",
            "Host Machine",
            "host-application",
            new ClientProfile(picture));

        Assert.Empty(context.Manager.GetAvailableHosts("host-application"));
        var visibleToOtherInstance = Assert.Single(
            context.Manager.GetAvailableHosts("other-application"));

        Assert.Equal(created.SessionId, visibleToOtherInstance.SessionId);
        Assert.Equal("host-application", visibleToOtherInstance.ApplicationInstanceId);
        Assert.Equal(picture, visibleToOtherInstance.ProfilePicturePng);
    }

    [Fact]
    public void DirectJoin_RejectsSelfButAllowsAnotherInstanceWithSameMachineProfile()
    {
        var context = CreateContext();
        var created = context.Manager.CreateHostSession(
            CreateDisplay(),
            "host-connection",
            "shared-machine-profile",
            "Host Machine",
            "host-application");

        var selfJoin = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "shared-machine-profile", "1.0.0"),
            "self-annotator-connection",
            "This Instance",
            "host-application");
        var otherInstanceJoin = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "shared-machine-profile", "1.0.0"),
            "other-annotator-connection",
            "Other Instance",
            "other-application");

        Assert.False(selfJoin.Response.Accepted);
        Assert.Contains("itself", selfJoin.Response.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(otherInstanceJoin.Response.Accepted);
    }

    [Fact]
    public void HostDisplayUpdate_IsReturnedForApprovedAnnotator()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        var changed = new DisplayDescriptor("display-1", "Display 1", 1_200, 1_920, 1d, 90);

        var result = context.Manager.UpdateHostDisplay(
            approved.Created.SessionId,
            "host-connection",
            changed);

        Assert.Equal(approved.AnnotatorConnectionId, result.AnnotatorConnectionId);
        Assert.Equal(changed, result.Display);
    }

    [Fact]
    public void HostClientSettingsUpdate_ChangesActiveSessionAndDirectoryImmediately()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        byte[] picture = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        var result = context.Manager.UpdateHostClientSettings(
            approved.Created.SessionId,
            "host-connection",
            "Updated Host",
            new ClientProfile(picture),
            2);

        Assert.Equal("Updated Host", result.State.HostDisplayName);
        Assert.Equal(picture, result.State.HostProfilePicturePng);
        Assert.Contains(approved.AnnotatorConnectionId, result.AnnotatorConnectionIds);
        var available = Assert.Single(context.Manager.GetAvailableHosts());
        Assert.Equal("Updated Host", available.DisplayName);
        Assert.Equal(picture, available.ProfilePicturePng);
    }

    [Fact]
    public void DiscoverableHost_RemainsAvailableAfterTheAbandonmentGrace()
    {
        var context = CreateContext();
        var created = CreateHost(context);
        context.TimeProvider.Advance(TimeSpan.FromMinutes(11));

        var expired = context.Manager.CollectExpiredSessions();

        Assert.Empty(expired);
        Assert.Equal(created.SessionId, Assert.Single(context.Manager.GetAvailableHosts()).SessionId);
        var directJoin = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "annotator-client", "1.0.0"),
            "annotator-connection",
            "Annotator Machine");
        Assert.True(directJoin.Response.Accepted);
    }

    [Fact]
    public void InvisibleHost_KeepsItsSessionAfterTheAbandonmentGrace()
    {
        var context = CreateContext();
        var created = CreateHost(context);
        context.Manager.SetHostDiscoverable(created.SessionId, "host-connection", false);
        context.TimeProvider.Advance(TimeSpan.FromMinutes(11));

        var expired = context.Manager.CollectExpiredSessions();

        Assert.Empty(expired);
        Assert.Equal(1, context.Manager.ActiveSessionCount);
        Assert.True(
            context.Manager.SetHostDiscoverable(created.SessionId, "host-connection", true));
        Assert.Equal(
            created.SessionId,
            Assert.Single(context.Manager.GetAvailableHosts()).SessionId);
    }

    [Fact]
    public void AbandonedInvisibleSession_IsCollectedAfterTheAbandonmentGrace()
    {
        var context = CreateContext();
        var created = CreateHost(context);
        context.Manager.SetHostDiscoverable(created.SessionId, "host-connection", false);
        _ = context.Manager.Disconnect("host-connection");
        context.TimeProvider.Advance(TimeSpan.FromMinutes(11));

        var expired = context.Manager.CollectExpiredSessions();

        Assert.Equal(created.SessionId, Assert.Single(expired).SessionId);
        Assert.Equal(0, context.Manager.ActiveSessionCount);
    }

    [Fact]
    public void Room_ScopesTheDirectoryAndJoinsToClientsThatShareIt()
    {
        var context = CreateContext();
        context.Manager.SetConnectionRoom("host-connection", "room-one");
        var created = CreateHost(context);
        context.Manager.SetConnectionRoom("insider-connection", "room-one");
        context.Manager.SetConnectionRoom("outsider-connection", "room-two");

        Assert.Equal(
            created.SessionId,
            Assert.Single(context.Manager.GetAvailableHosts(null, "insider-connection")).SessionId);
        Assert.Empty(context.Manager.GetAvailableHosts(null, "outsider-connection"));

        var outsiderJoin = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "outsider-client", "1.0.0"),
            "outsider-connection",
            "Outsider");
        Assert.False(outsiderJoin.Response.Accepted);

        var insiderJoin = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "insider-client", "1.0.0"),
            "insider-connection",
            "Insider");
        Assert.True(insiderJoin.Response.Accepted);
    }

    [Fact]
    public void ChangedRoom_TakesThePublishedHostOutOfTheOldRoom()
    {
        var context = CreateContext();
        context.Manager.SetConnectionRoom("host-connection", "room-one");
        var created = CreateHost(context);
        context.Manager.SetConnectionRoom("former-peer-connection", "room-one");
        Assert.Single(context.Manager.GetAvailableHosts(null, "former-peer-connection"));

        context.Manager.SetConnectionRoom("host-connection", "room-two");

        Assert.Empty(context.Manager.GetAvailableHosts(null, "former-peer-connection"));
        var staleJoin = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "former-peer-client", "1.0.0"),
            "former-peer-connection",
            "Former Peer");
        Assert.False(staleJoin.Response.Accepted);

        context.Manager.SetConnectionRoom("new-peer-connection", "room-two");
        Assert.Equal(
            created.SessionId,
            Assert.Single(context.Manager.GetAvailableHosts(null, "new-peer-connection")).SessionId);
    }

    [Fact]
    public void ChangedRoom_CancelsAJoinRequestThatNoLongerSharesTheRoom()
    {
        var context = CreateContext();
        context.Manager.SetConnectionRoom("host-connection", "room-one");
        var created = CreateHost(context);
        context.Manager.SetConnectionRoom("annotator-connection", "room-one");
        var join = JoinAnnotator(context, created);
        Assert.True(join.Response.Accepted);

        var change = context.Manager.SetConnectionRoom("host-connection", "room-two");

        Assert.Equal(
            "annotator-connection",
            change.CancelledJoinRequest?.CancelledAnnotatorRequestConnectionId);
        Assert.Equal("host-connection", change.CancelledJoinRequest?.HostConnectionId);

        // The request is gone from both sides: the host is listable again, and the former
        // requester is unbound rather than left waiting on an approval it can no longer get.
        context.Manager.SetConnectionRoom("new-peer-connection", "room-two");
        Assert.Single(context.Manager.GetAvailableHosts(null, "new-peer-connection"));
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.ApproveAnnotator(
                created.SessionId,
                "annotator-connection",
                "host-connection"));
    }

    [Fact]
    public void ChangedRoom_KeepsAnAnnotatorTheHostAlreadyApproved()
    {
        var context = CreateContext();
        context.Manager.SetConnectionRoom("host-connection", "room-one");
        var created = CreateHost(context);
        context.Manager.SetConnectionRoom("annotator-connection", "room-one");
        var join = JoinAnnotator(context, created);
        _ = context.Manager.ApproveAnnotator(
            created.SessionId,
            join.Annotator!.ConnectionId,
            "host-connection");

        var change = context.Manager.SetConnectionRoom("annotator-connection", "room-two");

        Assert.Null(change.CancelledJoinRequest);
        var relayed = context.Manager.AcceptPointer(
            "annotator-connection",
            CreatePointer(InitialTime, created.SessionId, 0));
        Assert.Equal(PointerRelayDisposition.Accepted, relayed.Disposition);
    }

    [Fact]
    public void ResumedHost_PublishesInTheRoomItsConnectionNamed()
    {
        var context = CreateContext();
        context.Manager.SetConnectionRoom("host-connection", "room-one");
        var created = CreateHost(context);
        _ = context.Manager.Disconnect("host-connection");

        context.Manager.SetConnectionRoom("resumed-connection", "room-two");
        _ = context.Manager.ResumeSession(
            "resumed-connection",
            new SessionResumeRequest(
                created.SessionId,
                ClientRole.Host,
                "host-client",
                created.Credential.SessionToken,
                created.Credential.ReconnectToken));

        context.Manager.SetConnectionRoom("former-peer-connection", "room-one");
        context.Manager.SetConnectionRoom("new-peer-connection", "room-two");
        Assert.Empty(context.Manager.GetAvailableHosts(null, "former-peer-connection"));
        Assert.Single(context.Manager.GetAvailableHosts(null, "new-peer-connection"));
    }

    [Fact]
    public void UnnamedRoom_PutsTheConnectionInTheDefaultOne()
    {
        var context = CreateContext();
        var created = CreateHost(context);

        // A client that never names a room, and one that names something unusable, both belong
        // in the default room rather than in a directory of their own.
        Assert.Equal(RoomName.Default, context.Manager.GetConnectionRoom("anyone"));
        Assert.Equal(RoomName.Default, context.Manager.SetConnectionRoom("blank", "   ").Room);
        Assert.Equal(
            RoomName.Default,
            context.Manager.SetConnectionRoom("too-long", new string('r', 65)).Room);
        Assert.Equal(
            created.SessionId,
            Assert.Single(context.Manager.GetAvailableHosts(null, "blank")).SessionId);
    }

    [Fact]
    public void Room_IgnoresCaseAndSurroundingSpaceSoTypedNamesStillMeet()
    {
        var context = CreateContext();
        context.Manager.SetConnectionRoom("host-connection", "Engineering");
        var created = CreateHost(context);
        context.Manager.SetConnectionRoom("peer-connection", "  engineering  ");

        Assert.Equal(
            created.SessionId,
            Assert.Single(context.Manager.GetAvailableHosts(null, "peer-connection")).SessionId);
    }

    [Fact]
    public void ConnectionRoom_IsReleasedWhenTheConnectionDrops()
    {
        var context = CreateContext();
        context.Manager.SetConnectionRoom("browser-connection", "room-one");

        _ = context.Manager.Disconnect("browser-connection");

        Assert.Equal(
            RoomName.Default,
            context.Manager.GetConnectionRoom("browser-connection"));
        Assert.Empty(context.Manager.GetAvailableHosts(null, "browser-connection"));
    }

    [Fact]
    public void CreateHostSession_FitsTheDefaultAnnotatorCountToTheRelayLimit()
    {
        var context = CreateContext(maximumAnnotatorsPerHost: 1);
        var created = CreateHost(context);
        ApproveDirectAnnotator(context, created, "annotator-one");

        var rejected = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "annotator-two", "1.0.0"),
            "annotator-two-connection",
            "Annotator Two");

        Assert.False(rejected.Response.Accepted);
        Assert.Contains("limit", rejected.Response.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.CreateHostSession(
                CreateDisplay(),
                "other-connection",
                "other-client",
                "Other Host",
                maximumAnnotatorConnections: 2));
    }

    [Fact]
    public void CreateHostSession_ReportsAnOversizedIdentifierAsAValidationFailure()
    {
        var context = CreateContext();

        var exception = Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.CreateHostSession(
                CreateDisplay(),
                "host-connection",
                "host-client",
                new string('n', 129)));

        Assert.Contains("128", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.Manager.ActiveSessionCount);
    }

    [Fact]
    public void CreateHostSession_IssuesHostOnlyCredential()
    {
        var context = CreateContext();

        var response = context.Manager.CreateHostSession(
            CreateDisplay(),
            "host-connection",
            "host-client",
            "Host Machine");

        Assert.True(response.SessionId.Length >= 43);
        Assert.True(response.SessionSecret.Length >= 43);
        Assert.Equal(ClientRole.Host, response.Credential.Role);
        Assert.Equal("host-client", response.Credential.ClientInstanceId);
        Assert.Equal(InitialTime.AddHours(8), response.Credential.ExpiresAt);
        Assert.Equal(1, context.Manager.ActiveSessionCount);
    }

    [Fact]
    public void SecondJoinRequest_ExposesNoSessionDataToTheRejectedClient()
    {
        var context = CreateContext();
        var created = CreateHost(context);

        var first = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "annotator-one", "1.0.0"),
            "annotator-connection-one",
            "Annotator One");
        var second = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "annotator-two", "1.0.0"),
            "annotator-connection-two",
            "Annotator Two");

        Assert.True(first.Response.Accepted);
        Assert.NotNull(first.Annotator);
        Assert.False(second.Response.Accepted);
        Assert.Null(second.Response.SessionId);
        Assert.Null(second.HostConnectionId);
        Assert.Null(second.Annotator);
    }

    [Fact]
    public void ApproveAnnotator_RequiresOwningHost()
    {
        var context = CreateContext();
        var created = CreateHost(context);
        var join = JoinAnnotator(context, created);

        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.ApproveAnnotator(
                created.SessionId,
                join.Annotator!.ConnectionId,
                "unauthorized-connection"));
    }

    [Fact]
    public void RejectAnnotator_RemovesPendingMembershipAndAllowsAnotherRequest()
    {
        var context = CreateContext();
        var created = CreateHost(context);
        var firstJoin = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "annotator-one", "1.0.0"),
            "annotator-connection-one",
            "Annotator One");

        var rejection = context.Manager.RejectAnnotator(
            created.SessionId,
            firstJoin.Annotator!.ConnectionId,
            "host-connection");
        var secondJoin = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "annotator-two", "1.0.0"),
            "annotator-connection-two",
            "Annotator Two");

        Assert.Equal("annotator-connection-one", rejection.AnnotatorConnectionId);
        Assert.True(secondJoin.Response.Accepted);
    }

    [Fact]
    public void ApprovedAnnotator_CanRelayPointerOnlyToHost()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);

        var result = context.Manager.AcceptPointer(
            approved.AnnotatorConnectionId,
            CreatePointer(context.TimeProvider.GetUtcNow(), approved.Created.SessionId, 0));

        Assert.Equal(PointerRelayDisposition.Accepted, result.Disposition);
        Assert.Equal("host-connection", result.HostConnectionId);
    }

    [Fact]
    public void ApprovedAnnotator_CanRelayValidatedGesturePointer()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        var pointer = CreatePointer(
            context.TimeProvider.GetUtcNow(),
            approved.Created.SessionId,
            0) with
        {
            Kind = PointerKind.PathStart,
            GestureId = Guid.NewGuid(),
        };

        var result = context.Manager.AcceptPointer(approved.AnnotatorConnectionId, pointer);

        Assert.Equal(PointerRelayDisposition.Accepted, result.Disposition);
    }

    [Fact]
    public void UnauthorizedConnection_CannotSendPointer()
    {
        var context = CreateContext();
        var created = CreateHost(context);

        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.AcceptPointer(
                "third-party",
                CreatePointer(context.TimeProvider.GetUtcNow(), created.SessionId, 0)));
    }

    [Fact]
    public void DuplicateSequence_IsIgnored()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        var pointer = CreatePointer(
            context.TimeProvider.GetUtcNow(),
            approved.Created.SessionId,
            7);

        var first = context.Manager.AcceptPointer(approved.AnnotatorConnectionId, pointer);
        var duplicate = context.Manager.AcceptPointer(approved.AnnotatorConnectionId, pointer with
        {
            EventId = Guid.NewGuid(),
        });

        Assert.Equal(PointerRelayDisposition.Accepted, first.Disposition);
        Assert.Equal(PointerRelayDisposition.IgnoredSequence, duplicate.Disposition);
    }

    [Fact]
    public void BurstRateLimit_RejectsThirtyFirstImmediateEvent()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        for (var sequence = 0; sequence < 30; sequence++)
        {
            var result = context.Manager.AcceptPointer(
                approved.AnnotatorConnectionId,
                CreatePointer(
                    context.TimeProvider.GetUtcNow(),
                    approved.Created.SessionId,
                    sequence));
            Assert.Equal(PointerRelayDisposition.Accepted, result.Disposition);
        }

        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.AcceptPointer(
                approved.AnnotatorConnectionId,
                CreatePointer(
                    context.TimeProvider.GetUtcNow(),
                    approved.Created.SessionId,
                    30)));
    }

    [Fact]
    public void BurstRateLimit_IsTrackedPerAnnotator()
    {
        var context = CreateContext();
        var created = context.Manager.CreateHostSession(
            CreateDisplay(),
            "host-connection",
            "host-client",
            "Host",
            maximumAnnotatorConnections: 2);
        ApproveDirectAnnotator(context, created, "annotator-one");
        ApproveDirectAnnotator(context, created, "annotator-two");

        for (var sequence = 0; sequence < 30; sequence++)
        {
            Assert.Equal(
                PointerRelayDisposition.Accepted,
                context.Manager.AcceptPointer(
                    "annotator-one-connection",
                    CreatePointer(InitialTime, created.SessionId, sequence)).Disposition);
        }

        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.AcceptPointer(
                "annotator-one-connection",
                CreatePointer(InitialTime, created.SessionId, 30)));
        Assert.Equal(
            PointerRelayDisposition.Accepted,
            context.Manager.AcceptPointer(
                "annotator-two-connection",
                CreatePointer(InitialTime, created.SessionId, 0)).Disposition);
    }

    [Fact]
    public void RateLimit_RefillsOverTime()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        for (var sequence = 0; sequence < 30; sequence++)
        {
            _ = context.Manager.AcceptPointer(
                approved.AnnotatorConnectionId,
                CreatePointer(
                    context.TimeProvider.GetUtcNow(),
                    approved.Created.SessionId,
                    sequence));
        }

        context.TimeProvider.Advance(TimeSpan.FromMilliseconds(50));
        var result = context.Manager.AcceptPointer(
            approved.AnnotatorConnectionId,
            CreatePointer(
                context.TimeProvider.GetUtcNow(),
                approved.Created.SessionId,
                30));

        Assert.Equal(PointerRelayDisposition.Accepted, result.Disposition);
    }

    [Fact]
    public void ActiveSessionExpiry_RejectsPointer()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        context.TimeProvider.Advance(TimeSpan.FromHours(8).Add(TimeSpan.FromMilliseconds(1)));

        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.AcceptPointer(
                approved.AnnotatorConnectionId,
                CreatePointer(
                    context.TimeProvider.GetUtcNow(),
                    approved.Created.SessionId,
                    0)));
        Assert.Equal(0, context.Manager.ActiveSessionCount);
    }

    [Fact]
    public void AnnotatorDisconnect_RevokesCredentialAndClearsConnectedState()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        var credential = approved.Approval.AnnotatorCredential;

        var disconnected = Assert.IsType<ConnectionDisconnectResult>(
            context.Manager.Disconnect(approved.AnnotatorConnectionId));

        Assert.Equal(ClientRole.Annotator, disconnected.DisconnectedRole);
        Assert.Equal("host-connection", disconnected.HostConnectionId);
        Assert.False(disconnected.State!.Approved);
        Assert.Empty(disconnected.State.ConnectedAnnotators!);
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.ResumeSession(
                "annotator-reconnected",
                new SessionResumeRequest(
                    credential.SessionId,
                    credential.Role,
                    credential.ClientInstanceId,
                    credential.SessionToken,
                    credential.ReconnectToken)));
    }

    [Fact]
    public void HostDisconnect_RevokesAnnotatorsAndResumeRequiresNewRequest()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);

        var disconnected = Assert.IsType<ConnectionDisconnectResult>(
            context.Manager.Disconnect("host-connection"));

        Assert.Equal(ClientRole.Host, disconnected.DisconnectedRole);
        Assert.Contains(
            approved.AnnotatorConnectionId,
            disconnected.AnnotatorConnectionIdsToEnd);
        Assert.False(disconnected.State!.Approved);
        Assert.Empty(context.Manager.GetAvailableHosts());
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.ResumeSession(
                "annotator-reconnected",
                new SessionResumeRequest(
                    approved.Approval.AnnotatorCredential.SessionId,
                    approved.Approval.AnnotatorCredential.Role,
                    approved.Approval.AnnotatorCredential.ClientInstanceId,
                    approved.Approval.AnnotatorCredential.SessionToken,
                    approved.Approval.AnnotatorCredential.ReconnectToken)));

        var resumedHost = context.Manager.ResumeSession(
            "host-reconnected",
            new SessionResumeRequest(
                approved.Created.Credential.SessionId,
                approved.Created.Credential.Role,
                approved.Created.Credential.ClientInstanceId,
                approved.Created.Credential.SessionToken,
                approved.Created.Credential.ReconnectToken));
        var freshRequest = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(
                approved.Created.SessionId,
                "new-annotator-client",
                "1.0.0"),
            "new-annotator-connection",
            "New Annotator");

        Assert.False(resumedHost.State.Approved);
        Assert.True(freshRequest.Response.Accepted);
    }

    [Fact]
    public void PendingAnnotatorDisconnect_CancelsRequestAndAllowsReplacement()
    {
        var context = CreateContext();
        var created = CreateHost(context);
        var pending = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "annotator-client", "1.0.0"),
            "pending-connection",
            "Pending Annotator");

        var disconnected = Assert.IsType<ConnectionDisconnectResult>(
            context.Manager.Disconnect("pending-connection"));
        var replacement = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "replacement-client", "1.0.0"),
            "replacement-connection",
            "Replacement Annotator");

        Assert.True(pending.Response.Accepted);
        Assert.Equal(
            "pending-connection",
            disconnected.CancelledAnnotatorRequestConnectionId);
        Assert.True(replacement.Response.Accepted);
    }

    [Fact]
    public void HostResume_UpdatesApplicationInstanceUsedForSelfFiltering()
    {
        var context = CreateContext();
        var created = context.Manager.CreateHostSession(
            CreateDisplay(),
            "host-connection",
            "host-client",
            "Host",
            "old-application");
        context.Manager.Disconnect("host-connection");

        _ = context.Manager.ResumeSession(
            "host-reconnected",
            new SessionResumeRequest(
                created.Credential.SessionId,
                created.Credential.Role,
                created.Credential.ClientInstanceId,
                created.Credential.SessionToken,
                created.Credential.ReconnectToken),
            "new-application");

        Assert.Empty(context.Manager.GetAvailableHosts("new-application"));
        Assert.Single(context.Manager.GetAvailableHosts("old-application"));
    }

    [Fact]
    public void HostAcknowledgement_RelaysOnlyToApprovedAnnotator()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        var pointer = CreatePointer(InitialTime, approved.Created.SessionId, 1);
        _ = context.Manager.AcceptPointer(approved.AnnotatorConnectionId, pointer);
        var acknowledgement = new PointerAcknowledgement(pointer.EventId, 1000);

        var result = context.Manager.AcceptAcknowledgement(
            "host-connection",
            acknowledgement);

        Assert.Equal(approved.AnnotatorConnectionId, result.AnnotatorConnectionId);
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.AcceptAcknowledgement(
                approved.AnnotatorConnectionId,
                acknowledgement));
    }

    [Fact]
    public void MultipleAnnotators_HonorLimitAndRouteSequencesAndAcknowledgementsIndependently()
    {
        var context = CreateContext();
        var created = context.Manager.CreateHostSession(
            CreateDisplay(),
            "host-connection",
            "host-client",
            "Host",
            maximumAnnotatorConnections: 2);
        var firstJoin = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "annotator-one", "1.0.0"),
            "annotator-one-connection",
            "Annotator One");
        _ = context.Manager.ApproveAnnotator(
            created.SessionId,
            firstJoin.Annotator!.ConnectionId,
            "host-connection");

        Assert.Single(context.Manager.GetAvailableHosts());
        var secondJoin = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "annotator-two", "1.0.0"),
            "annotator-two-connection",
            "Annotator Two");
        var secondApproval = context.Manager.ApproveAnnotator(
            created.SessionId,
            secondJoin.Annotator!.ConnectionId,
            "host-connection");

        Assert.Equal(2, secondApproval.State.ConnectedAnnotators?.Length);
        Assert.Empty(context.Manager.GetAvailableHosts());
        var rejectedThird = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "annotator-three", "1.0.0"),
            "annotator-three-connection",
            "Annotator Three");
        Assert.False(rejectedThird.Response.Accepted);
        Assert.Contains("limit", rejectedThird.Response.Reason, StringComparison.OrdinalIgnoreCase);

        var firstPointer = CreatePointer(InitialTime, created.SessionId, 7);
        var secondPointer = CreatePointer(InitialTime, created.SessionId, 7);
        Assert.Equal(
            PointerRelayDisposition.Accepted,
            context.Manager.AcceptPointer("annotator-one-connection", firstPointer).Disposition);
        Assert.Equal(
            PointerRelayDisposition.Accepted,
            context.Manager.AcceptPointer("annotator-two-connection", secondPointer).Disposition);
        Assert.Equal(
            "annotator-one-connection",
            context.Manager.AcceptAcknowledgement(
                "host-connection",
                new PointerAcknowledgement(firstPointer.EventId, 1000)).AnnotatorConnectionId);
        Assert.Equal(
            "annotator-two-connection",
            context.Manager.AcceptAcknowledgement(
                "host-connection",
                new PointerAcknowledgement(secondPointer.EventId, 1001)).AnnotatorConnectionId);

        var firstEnded = context.Manager.EndSession(
            created.SessionId,
            "annotator-one-connection");
        Assert.True(firstEnded.HostPreserved);
        Assert.Equal(
            ["Annotator Two"],
            firstEnded.State!.ConnectedAnnotators!
                .Select(annotator => annotator.DisplayName)
                .ToArray());
        Assert.Single(context.Manager.GetAvailableHosts());
    }

    [Fact]
    public void AnnotatorEnd_PreservesAvailableHostForAnotherRequest()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);

        var result = context.Manager.EndSession(
            approved.Created.SessionId,
            approved.AnnotatorConnectionId);

        Assert.Equal(approved.Created.SessionId, result.SessionId);
        Assert.True(result.HostPreserved);
        Assert.DoesNotContain("host-connection", result.ConnectionIds);
        Assert.Contains(approved.AnnotatorConnectionId, result.ConnectionIds);
        Assert.Equal(1, context.Manager.ActiveSessionCount);
        Assert.Equal(
            approved.Created.SessionId,
            Assert.Single(context.Manager.GetAvailableHosts()).SessionId);
        var nextJoin = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(approved.Created.SessionId, "next-annotator", "1.0.0"),
            "next-annotator-connection",
            "Next Annotator");
        Assert.True(nextJoin.Response.Accepted);
    }

    [Fact]
    public void PendingAnnotatorEnd_WithdrawsRequestAndReleasesTheHost()
    {
        var context = CreateContext();
        var created = CreateHost(context);
        var join = JoinAnnotator(context, created);

        var result = context.Manager.EndSession(created.SessionId, "annotator-connection");

        Assert.True(result.HostPreserved);
        Assert.Equal("annotator-connection", result.CancelledAnnotatorRequestConnectionId);
        Assert.Contains("annotator-connection", result.ConnectionIds);
        Assert.False(result.State!.Approved);
        Assert.Equal(1, context.Manager.ActiveSessionCount);
        Assert.Equal(
            created.SessionId,
            Assert.Single(context.Manager.GetAvailableHosts()).SessionId);
        Assert.Equal("annotator-connection", join.Annotator!.ConnectionId);
        var nextJoin = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "next-annotator", "1.0.0"),
            "next-annotator-connection",
            "Next Annotator");
        Assert.True(nextJoin.Response.Accepted);
    }

    [Fact]
    public void PendingAnnotatorEnd_CannotEndTheHostSession()
    {
        var context = CreateContext();
        var created = CreateHost(context);
        _ = JoinAnnotator(context, created);

        context.Manager.EndSession(created.SessionId, "annotator-connection");

        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.EndSession(created.SessionId, "annotator-connection"));
        Assert.Equal(1, context.Manager.ActiveSessionCount);
    }

    [Fact]
    public void HostEnd_RemovesHostAndAnnotator()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);

        var result = context.Manager.EndSession(
            approved.Created.SessionId,
            "host-connection");

        Assert.False(result.HostPreserved);
        Assert.Contains("host-connection", result.ConnectionIds);
        Assert.Contains(approved.AnnotatorConnectionId, result.ConnectionIds);
        Assert.Equal(0, context.Manager.ActiveSessionCount);
    }

    [Fact]
    public void HostDisconnectAnnotators_PreservesAvailability()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);

        var result = context.Manager.DisconnectAnnotators(
            approved.Created.SessionId,
            "host-connection");

        Assert.True(result.HostPreserved);
        Assert.Contains(approved.AnnotatorConnectionId, result.ConnectionIds);
        Assert.Equal(1, context.Manager.ActiveSessionCount);
        Assert.Equal(
            approved.Created.SessionId,
            Assert.Single(context.Manager.GetAvailableHosts()).SessionId);
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.AcceptPointer(
                approved.AnnotatorConnectionId,
                CreatePointer(InitialTime, approved.Created.SessionId, 1)));
    }

    [Fact]
    public void AnnotatorThatChangedItsPassword_StillReportsTheGroupItsSessionIsListedIn()
    {
        var context = CreateContext();
        context.Manager.SetConnectionRoom("host-connection", "shared-key");
        context.Manager.SetConnectionRoom("annotator-connection", "shared-key");
        var approved = CreateApprovedSession(context);

        // An approved annotator keeps its place when it changes its own password, so from here
        // its connection group and the group its session is published in disagree.
        context.Manager.SetConnectionRoom("annotator-connection", "private-key");
        var ended = context.Manager.EndSession(
            approved.Created.SessionId,
            "annotator-connection");

        Assert.Equal("private-key", context.Manager.GetConnectionRoom("annotator-connection"));
        Assert.Equal("shared-key", ended.Room);
        Assert.True(ended.HostPreserved);
    }

    [Fact]
    public void CollectedSession_ReportsTheGroupThatHasToRereadTheDirectory()
    {
        var context = CreateContext();
        context.Manager.SetConnectionRoom("host-connection", "shared-key");
        _ = CreateHost(context);

        context.TimeProvider.Advance(TimeSpan.FromHours(9));
        var collected = Assert.Single(context.Manager.CollectExpiredSessions());

        Assert.Equal("shared-key", collected.Room);
        Assert.Equal(0, context.Manager.ActiveSessionCount);
    }

    [Fact]
    public void PausedAnnotator_StopsRelayingPointersUntilItIsResumed()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);

        var paused = context.Manager.SetAnnotatorPaused(
            approved.Created.SessionId,
            "host-connection",
            "annotator-client",
            paused: true);

        Assert.True(paused.Paused);
        Assert.Equal([approved.AnnotatorConnectionId], paused.AnnotatorConnectionIds);
        Assert.True(Assert.Single(paused.State.ConnectedAnnotators!).IsPaused);
        Assert.Equal(
            "annotator-client",
            Assert.Single(paused.State.ConnectedAnnotators!).AnnotatorId);

        var dropped = context.Manager.AcceptPointer(
            approved.AnnotatorConnectionId,
            CreatePointer(InitialTime, approved.Created.SessionId, 0));

        Assert.Equal(PointerRelayDisposition.Paused, dropped.Disposition);

        var resumed = context.Manager.SetAnnotatorPaused(
            approved.Created.SessionId,
            "host-connection",
            "annotator-client",
            paused: false);
        var relayed = context.Manager.AcceptPointer(
            approved.AnnotatorConnectionId,
            CreatePointer(InitialTime, approved.Created.SessionId, 1));

        Assert.False(Assert.Single(resumed.State.ConnectedAnnotators!).IsPaused);
        Assert.Equal(PointerRelayDisposition.Accepted, relayed.Disposition);
        Assert.Equal("annotator-client", relayed.AnnotatorId);
    }

    [Fact]
    public void PauseWithoutAnAnnotatorId_AppliesToEveryConnectedAnnotator()
    {
        var context = CreateContext(maximumAnnotatorsPerHost: 2);
        var created = CreateHost(context);
        ApproveDirectAnnotator(context, created, "annotator-one");
        ApproveDirectAnnotator(context, created, "annotator-two");

        var paused = context.Manager.SetAnnotatorPaused(
            created.SessionId,
            "host-connection",
            annotatorId: null,
            paused: true);

        Assert.Equal(2, paused.AnnotatorConnectionIds.Count);
        Assert.All(
            paused.State.ConnectedAnnotators!,
            annotator => Assert.True(annotator.IsPaused));
    }

    [Fact]
    public void DisconnectAnnotator_EndsOnlyTheNamedAnnotator()
    {
        var context = CreateContext(maximumAnnotatorsPerHost: 2);
        var created = CreateHost(context);
        ApproveDirectAnnotator(context, created, "annotator-one");
        ApproveDirectAnnotator(context, created, "annotator-two");

        var disconnected = context.Manager.DisconnectAnnotator(
            created.SessionId,
            "host-connection",
            "annotator-one");

        Assert.True(disconnected.HostPreserved);
        Assert.Equal(["annotator-one-connection"], disconnected.AnnotatorConnectionIds!);
        Assert.Equal(
            "annotator-two",
            Assert.Single(disconnected.State!.ConnectedAnnotators!).AnnotatorId);
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.DisconnectAnnotator(
                created.SessionId,
                "host-connection",
                "annotator-one"));
    }

    [Fact]
    public void PauseOrDisconnectOfOneAnnotator_RequiresTheHostConnection()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);

        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.SetAnnotatorPaused(
                approved.Created.SessionId,
                approved.AnnotatorConnectionId,
                "annotator-client",
                paused: true));
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.DisconnectAnnotator(
                approved.Created.SessionId,
                approved.AnnotatorConnectionId,
                "annotator-client"));
    }

    private static TestContext CreateContext(int maximumAnnotatorsPerHost = 16)
    {
        var timeProvider = new ManualTimeProvider(InitialTime);
        var manager = new SessionManager(
            Options.Create(new SessionOptions
            {
                AbandonedSessionLifetimeMinutes = 10,
                MaximumSessionHours = 8,
                SequenceWindowSize = 64,
                MaximumAnnotatorsPerHost = maximumAnnotatorsPerHost,
            }),
            Options.Create(new PointerRateLimitOptions
            {
                EventsPerSecond = 20,
                BurstSize = 30,
            }),
            new SessionSecretGenerator(),
            timeProvider);
        return new TestContext(manager, timeProvider);
    }

    private static CreateSessionResponse CreateHost(TestContext context) =>
        context.Manager.CreateHostSession(
            CreateDisplay(),
            "host-connection",
            "host-client",
            "Host Machine");

    private static JoinSessionResult JoinAnnotator(
        TestContext context,
        CreateSessionResponse created) =>
        context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, "annotator-client", "1.0.0"),
            "annotator-connection",
            "Annotator Machine");

    private static void ApproveDirectAnnotator(
        TestContext context,
        CreateSessionResponse created,
        string clientInstanceId)
    {
        var join = context.Manager.RequestToJoinHost(
            new DirectJoinRequest(created.SessionId, clientInstanceId, "1.0.0"),
            $"{clientInstanceId}-connection",
            clientInstanceId);
        _ = context.Manager.ApproveAnnotator(
            created.SessionId,
            join.Annotator!.ConnectionId,
            "host-connection");
    }

    private static ApprovedContext CreateApprovedSession(TestContext context)
    {
        var created = CreateHost(context);
        var join = JoinAnnotator(context, created);
        var approval = context.Manager.ApproveAnnotator(
            created.SessionId,
            join.Annotator!.ConnectionId,
            "host-connection");
        return new ApprovedContext(
            created,
            approval,
            join.Annotator.ConnectionId);
    }

    private static DisplayDescriptor CreateDisplay() => new(
        "display-1",
        "Display 1",
        1_920,
        1_080,
        1d,
        0);

    private static PointerEventMessage CreatePointer(
        DateTimeOffset now,
        string sessionId,
        long sequence) => new(
        Guid.NewGuid(),
        sessionId,
        sequence,
        0.25d,
        0.75d,
        PointerKind.Click,
        now.ToUnixTimeMilliseconds(),
        2_000);

    private sealed record TestContext(
        SessionManager Manager,
        ManualTimeProvider TimeProvider);

    private sealed record ApprovedContext(
        CreateSessionResponse Created,
        ApproveAnnotatorResult Approval,
        string AnnotatorConnectionId);
}
