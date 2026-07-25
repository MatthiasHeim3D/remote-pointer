using Microsoft.Extensions.Options;
using RemotePointer.Contracts.Messages;
using RemotePointer.Server.RateLimiting;
using RemotePointer.Server.Sessions;

namespace RemotePointer.Server.Tests.Sessions;

public sealed class SessionManagerTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Receiver_IsAutomaticallyDiscoverableAndDirectJoinStillRequiresApproval()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);

        var available = Assert.Single(context.Manager.GetAvailableReceivers());
        Assert.Equal(created.SessionId, available.SessionId);
        Assert.Equal("Receiver Machine", available.DisplayName);

        var join = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "presenter-client", "1.0.0"),
            "presenter-connection",
            "Presenter Machine");

        Assert.True(join.Response.Accepted);
        Assert.NotNull(join.Presenter);
        Assert.Empty(context.Manager.GetAvailableReceivers());
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.AcceptPointer(
                "presenter-connection",
                CreatePointer(InitialTime, created.SessionId, 0)));
    }

    [Fact]
    public void PresenterProfilePicture_IsIncludedInApprovalAndConnectedState()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);
        byte[] picture = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        var join = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(
                created.SessionId,
                "presenter-client",
                "1.0.0",
                new ClientProfile(picture)),
            "presenter-connection",
            "Presenter Machine");
        var approval = context.Manager.ApprovePresenter(
            created.SessionId,
            join.Presenter!.ConnectionId,
            "receiver-connection");

        Assert.Equal(picture, join.Presenter.ProfilePicturePng);
        var connectedPresenter = Assert.Single(approval.State.ConnectedPresenters!);
        Assert.Equal("Presenter Machine", connectedPresenter.DisplayName);
        Assert.Equal(picture, connectedPresenter.ProfilePicturePng);
    }

    [Fact]
    public void Discovery_ExcludesOnlySameApplicationInstanceAndIncludesProfilePicture()
    {
        var context = CreateContext();
        byte[] picture = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var created = context.Manager.CreateReceiverSession(
            CreateDisplay(),
            "receiver-connection",
            "shared-machine-profile",
            "Receiver Machine",
            "receiver-application",
            new ClientProfile(picture));

        Assert.Empty(context.Manager.GetAvailableReceivers("receiver-application"));
        var visibleToOtherInstance = Assert.Single(
            context.Manager.GetAvailableReceivers("other-application"));

        Assert.Equal(created.SessionId, visibleToOtherInstance.SessionId);
        Assert.Equal("receiver-application", visibleToOtherInstance.ApplicationInstanceId);
        Assert.Equal(picture, visibleToOtherInstance.ProfilePicturePng);
    }

    [Fact]
    public void DirectJoin_RejectsSelfButAllowsAnotherInstanceWithSameMachineProfile()
    {
        var context = CreateContext();
        var created = context.Manager.CreateReceiverSession(
            CreateDisplay(),
            "receiver-connection",
            "shared-machine-profile",
            "Receiver Machine",
            "receiver-application");

        var selfJoin = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "shared-machine-profile", "1.0.0"),
            "self-presenter-connection",
            "This Instance",
            "receiver-application");
        var otherInstanceJoin = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "shared-machine-profile", "1.0.0"),
            "other-presenter-connection",
            "Other Instance",
            "other-application");

        Assert.False(selfJoin.Response.Accepted);
        Assert.Contains("itself", selfJoin.Response.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(otherInstanceJoin.Response.Accepted);
    }

    [Fact]
    public void ReceiverDisplayUpdate_IsReturnedForApprovedPresenter()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        var changed = new DisplayDescriptor("display-1", "Display 1", 1_200, 1_920, 1d, 90);

        var result = context.Manager.UpdateReceiverDisplay(
            approved.Created.SessionId,
            "receiver-connection",
            changed);

        Assert.Equal(approved.PresenterConnectionId, result.PresenterConnectionId);
        Assert.Equal(changed, result.Display);
    }

    [Fact]
    public void ReceiverClientSettingsUpdate_ChangesActiveSessionAndDirectoryImmediately()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        byte[] picture = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        var result = context.Manager.UpdateReceiverClientSettings(
            approved.Created.SessionId,
            "receiver-connection",
            "Updated Receiver",
            new ClientProfile(picture),
            2);

        Assert.Equal("Updated Receiver", result.State.ReceiverDisplayName);
        Assert.Equal(picture, result.State.ReceiverProfilePicturePng);
        Assert.Contains(approved.PresenterConnectionId, result.PresenterConnectionIds);
        var available = Assert.Single(context.Manager.GetAvailableReceivers());
        Assert.Equal("Updated Receiver", available.DisplayName);
        Assert.Equal(picture, available.ProfilePicturePng);
    }

    [Fact]
    public void DiscoverableReceiver_RemainsAvailableAfterTheAbandonmentGrace()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);
        context.TimeProvider.Advance(TimeSpan.FromMinutes(11));

        var expired = context.Manager.CollectExpiredSessions();

        Assert.Empty(expired);
        Assert.Equal(created.SessionId, Assert.Single(context.Manager.GetAvailableReceivers()).SessionId);
        var directJoin = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "presenter-client", "1.0.0"),
            "presenter-connection",
            "Presenter Machine");
        Assert.True(directJoin.Response.Accepted);
    }

    [Fact]
    public void InvisibleReceiver_KeepsItsSessionAfterTheAbandonmentGrace()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);
        context.Manager.SetReceiverDiscoverable(created.SessionId, "receiver-connection", false);
        context.TimeProvider.Advance(TimeSpan.FromMinutes(11));

        var expired = context.Manager.CollectExpiredSessions();

        Assert.Empty(expired);
        Assert.Equal(1, context.Manager.ActiveSessionCount);
        Assert.True(
            context.Manager.SetReceiverDiscoverable(created.SessionId, "receiver-connection", true));
        Assert.Equal(
            created.SessionId,
            Assert.Single(context.Manager.GetAvailableReceivers()).SessionId);
    }

    [Fact]
    public void AbandonedInvisibleSession_IsCollectedAfterTheAbandonmentGrace()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);
        context.Manager.SetReceiverDiscoverable(created.SessionId, "receiver-connection", false);
        _ = context.Manager.Disconnect("receiver-connection");
        context.TimeProvider.Advance(TimeSpan.FromMinutes(11));

        var expired = context.Manager.CollectExpiredSessions();

        Assert.Equal(created.SessionId, Assert.Single(expired).SessionId);
        Assert.Equal(0, context.Manager.ActiveSessionCount);
    }

    [Fact]
    public void ServerPassword_ScopesTheDirectoryAndJoinsToClientsThatShareIt()
    {
        var context = CreateContext(requireServerPassword: true);
        context.Manager.SetConnectionGroup("receiver-connection", "group-one");
        var created = CreateReceiver(context);
        context.Manager.SetConnectionGroup("insider-connection", "group-one");
        context.Manager.SetConnectionGroup("outsider-connection", "group-two");

        Assert.Equal(
            created.SessionId,
            Assert.Single(context.Manager.GetAvailableReceivers(null, "insider-connection")).SessionId);
        Assert.Empty(context.Manager.GetAvailableReceivers(null, "outsider-connection"));

        var outsiderJoin = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "outsider-client", "1.0.0"),
            "outsider-connection",
            "Outsider");
        Assert.False(outsiderJoin.Response.Accepted);

        var insiderJoin = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "insider-client", "1.0.0"),
            "insider-connection",
            "Insider");
        Assert.True(insiderJoin.Response.Accepted);
    }

    [Fact]
    public void ChangedServerPassword_TakesThePublishedReceiverOutOfTheOldGroup()
    {
        var context = CreateContext(requireServerPassword: true);
        context.Manager.SetConnectionGroup("receiver-connection", "group-one");
        var created = CreateReceiver(context);
        context.Manager.SetConnectionGroup("former-peer-connection", "group-one");
        Assert.Single(context.Manager.GetAvailableReceivers(null, "former-peer-connection"));

        context.Manager.SetConnectionGroup("receiver-connection", "group-two");

        Assert.Empty(context.Manager.GetAvailableReceivers(null, "former-peer-connection"));
        var staleJoin = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "former-peer-client", "1.0.0"),
            "former-peer-connection",
            "Former Peer");
        Assert.False(staleJoin.Response.Accepted);

        context.Manager.SetConnectionGroup("new-peer-connection", "group-two");
        Assert.Equal(
            created.SessionId,
            Assert.Single(context.Manager.GetAvailableReceivers(null, "new-peer-connection")).SessionId);
    }

    [Fact]
    public void ChangedServerPassword_CancelsAJoinRequestThatNoLongerSharesTheGroup()
    {
        var context = CreateContext(requireServerPassword: true);
        context.Manager.SetConnectionGroup("receiver-connection", "group-one");
        var created = CreateReceiver(context);
        context.Manager.SetConnectionGroup("presenter-connection", "group-one");
        var join = JoinPresenter(context, created);
        Assert.True(join.Response.Accepted);

        var change = context.Manager.SetConnectionGroup("receiver-connection", "group-two");

        Assert.Equal(
            "presenter-connection",
            change.CancelledJoinRequest?.CancelledPresenterRequestConnectionId);
        Assert.Equal("receiver-connection", change.CancelledJoinRequest?.ReceiverConnectionId);

        // The request is gone from both sides: the receiver is listable again, and the former
        // requester is unbound rather than left waiting on an approval it can no longer get.
        context.Manager.SetConnectionGroup("new-peer-connection", "group-two");
        Assert.Single(context.Manager.GetAvailableReceivers(null, "new-peer-connection"));
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.ApprovePresenter(
                created.SessionId,
                "presenter-connection",
                "receiver-connection"));
    }

    [Fact]
    public void ChangedServerPassword_KeepsAPresenterTheReceiverAlreadyApproved()
    {
        var context = CreateContext(requireServerPassword: true);
        context.Manager.SetConnectionGroup("receiver-connection", "group-one");
        var created = CreateReceiver(context);
        context.Manager.SetConnectionGroup("presenter-connection", "group-one");
        var join = JoinPresenter(context, created);
        _ = context.Manager.ApprovePresenter(
            created.SessionId,
            join.Presenter!.ConnectionId,
            "receiver-connection");

        var change = context.Manager.SetConnectionGroup("presenter-connection", "group-two");

        Assert.Null(change.CancelledJoinRequest);
        var relayed = context.Manager.AcceptPointer(
            "presenter-connection",
            CreatePointer(InitialTime, created.SessionId, 0));
        Assert.Equal(PointerRelayDisposition.Accepted, relayed.Disposition);
    }

    [Fact]
    public void ResumedReceiver_PublishesUnderThePasswordItsConnectionPresented()
    {
        var context = CreateContext(requireServerPassword: true);
        context.Manager.SetConnectionGroup("receiver-connection", "group-one");
        var created = CreateReceiver(context);
        _ = context.Manager.Disconnect("receiver-connection");

        context.Manager.SetConnectionGroup("resumed-connection", "group-two");
        _ = context.Manager.ResumeSession(
            "resumed-connection",
            new SessionResumeRequest(
                created.SessionId,
                ClientRole.Receiver,
                "receiver-client",
                created.Credential.SessionToken,
                created.Credential.ReconnectToken));

        context.Manager.SetConnectionGroup("former-peer-connection", "group-one");
        context.Manager.SetConnectionGroup("new-peer-connection", "group-two");
        Assert.Empty(context.Manager.GetAvailableReceivers(null, "former-peer-connection"));
        Assert.Single(context.Manager.GetAvailableReceivers(null, "new-peer-connection"));
    }

    [Fact]
    public void ServerPassword_IsRequiredBeforeAnythingIsPublishedOrListed()
    {
        var context = CreateContext(requireServerPassword: true);

        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.SetConnectionGroup("receiver-connection", null));
        Assert.ThrowsAny<InvalidOperationException>(() => CreateReceiver(context));
        Assert.Empty(context.Manager.GetAvailableReceivers(null, "receiver-connection"));
        Assert.True(context.Manager.ServerPasswordRequired);
    }

    [Fact]
    public void OpenRelay_KeepsPasswordlessClientsInOneSharedGroup()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);

        Assert.Equal(SessionManager.OpenGroupKey, context.Manager.GetConnectionGroup("anyone"));
        Assert.Equal(
            created.SessionId,
            Assert.Single(context.Manager.GetAvailableReceivers(null, "anyone")).SessionId);
    }

    [Fact]
    public void ConnectionGroup_IsReleasedWhenTheConnectionDrops()
    {
        var context = CreateContext(requireServerPassword: true);
        context.Manager.SetConnectionGroup("browser-connection", "group-one");

        _ = context.Manager.Disconnect("browser-connection");

        Assert.Equal(
            SessionManager.OpenGroupKey,
            context.Manager.GetConnectionGroup("browser-connection"));
        Assert.Empty(context.Manager.GetAvailableReceivers(null, "browser-connection"));
    }

    [Fact]
    public void CreateReceiverSession_FitsTheDefaultPresenterCountToTheRelayLimit()
    {
        var context = CreateContext(maximumPresentersPerReceiver: 1);
        var created = CreateReceiver(context);
        ApproveDirectPresenter(context, created, "presenter-one");

        var rejected = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "presenter-two", "1.0.0"),
            "presenter-two-connection",
            "Presenter Two");

        Assert.False(rejected.Response.Accepted);
        Assert.Contains("limit", rejected.Response.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.CreateReceiverSession(
                CreateDisplay(),
                "other-connection",
                "other-client",
                "Other Receiver",
                maximumPresenterConnections: 2));
    }

    [Fact]
    public void CreateReceiverSession_ReportsAnOversizedIdentifierAsAValidationFailure()
    {
        var context = CreateContext();

        var exception = Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.CreateReceiverSession(
                CreateDisplay(),
                "receiver-connection",
                "receiver-client",
                new string('n', 129)));

        Assert.Contains("128", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.Manager.ActiveSessionCount);
    }

    [Fact]
    public void CreateReceiverSession_IssuesReceiverOnlyCredential()
    {
        var context = CreateContext();

        var response = context.Manager.CreateReceiverSession(
            CreateDisplay(),
            "receiver-connection",
            "receiver-client",
            "Receiver Machine");

        Assert.True(response.SessionId.Length >= 43);
        Assert.True(response.SessionSecret.Length >= 43);
        Assert.Equal(ClientRole.Receiver, response.Credential.Role);
        Assert.Equal("receiver-client", response.Credential.ClientInstanceId);
        Assert.Equal(InitialTime.AddHours(8), response.Credential.ExpiresAt);
        Assert.Equal(1, context.Manager.ActiveSessionCount);
    }

    [Fact]
    public void SecondJoinRequest_ExposesNoSessionDataToTheRejectedClient()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);

        var first = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "presenter-one", "1.0.0"),
            "presenter-connection-one",
            "Presenter One");
        var second = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "presenter-two", "1.0.0"),
            "presenter-connection-two",
            "Presenter Two");

        Assert.True(first.Response.Accepted);
        Assert.NotNull(first.Presenter);
        Assert.False(second.Response.Accepted);
        Assert.Null(second.Response.SessionId);
        Assert.Null(second.ReceiverConnectionId);
        Assert.Null(second.Presenter);
    }

    [Fact]
    public void ApprovePresenter_RequiresOwningReceiver()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);
        var join = JoinPresenter(context, created);

        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.ApprovePresenter(
                created.SessionId,
                join.Presenter!.ConnectionId,
                "unauthorized-connection"));
    }

    [Fact]
    public void RejectPresenter_RemovesPendingMembershipAndAllowsAnotherRequest()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);
        var firstJoin = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "presenter-one", "1.0.0"),
            "presenter-connection-one",
            "Presenter One");

        var rejection = context.Manager.RejectPresenter(
            created.SessionId,
            firstJoin.Presenter!.ConnectionId,
            "receiver-connection");
        var secondJoin = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "presenter-two", "1.0.0"),
            "presenter-connection-two",
            "Presenter Two");

        Assert.Equal("presenter-connection-one", rejection.PresenterConnectionId);
        Assert.True(secondJoin.Response.Accepted);
    }

    [Fact]
    public void ApprovedPresenter_CanRelayPointerOnlyToReceiver()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);

        var result = context.Manager.AcceptPointer(
            approved.PresenterConnectionId,
            CreatePointer(context.TimeProvider.GetUtcNow(), approved.Created.SessionId, 0));

        Assert.Equal(PointerRelayDisposition.Accepted, result.Disposition);
        Assert.Equal("receiver-connection", result.ReceiverConnectionId);
    }

    [Fact]
    public void ApprovedPresenter_CanRelayValidatedGesturePointer()
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

        var result = context.Manager.AcceptPointer(approved.PresenterConnectionId, pointer);

        Assert.Equal(PointerRelayDisposition.Accepted, result.Disposition);
    }

    [Fact]
    public void UnauthorizedConnection_CannotSendPointer()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);

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

        var first = context.Manager.AcceptPointer(approved.PresenterConnectionId, pointer);
        var duplicate = context.Manager.AcceptPointer(approved.PresenterConnectionId, pointer with
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
                approved.PresenterConnectionId,
                CreatePointer(
                    context.TimeProvider.GetUtcNow(),
                    approved.Created.SessionId,
                    sequence));
            Assert.Equal(PointerRelayDisposition.Accepted, result.Disposition);
        }

        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.AcceptPointer(
                approved.PresenterConnectionId,
                CreatePointer(
                    context.TimeProvider.GetUtcNow(),
                    approved.Created.SessionId,
                    30)));
    }

    [Fact]
    public void BurstRateLimit_IsTrackedPerPresenter()
    {
        var context = CreateContext();
        var created = context.Manager.CreateReceiverSession(
            CreateDisplay(),
            "receiver-connection",
            "receiver-client",
            "Receiver",
            maximumPresenterConnections: 2);
        ApproveDirectPresenter(context, created, "presenter-one");
        ApproveDirectPresenter(context, created, "presenter-two");

        for (var sequence = 0; sequence < 30; sequence++)
        {
            Assert.Equal(
                PointerRelayDisposition.Accepted,
                context.Manager.AcceptPointer(
                    "presenter-one-connection",
                    CreatePointer(InitialTime, created.SessionId, sequence)).Disposition);
        }

        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.AcceptPointer(
                "presenter-one-connection",
                CreatePointer(InitialTime, created.SessionId, 30)));
        Assert.Equal(
            PointerRelayDisposition.Accepted,
            context.Manager.AcceptPointer(
                "presenter-two-connection",
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
                approved.PresenterConnectionId,
                CreatePointer(
                    context.TimeProvider.GetUtcNow(),
                    approved.Created.SessionId,
                    sequence));
        }

        context.TimeProvider.Advance(TimeSpan.FromMilliseconds(50));
        var result = context.Manager.AcceptPointer(
            approved.PresenterConnectionId,
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
                approved.PresenterConnectionId,
                CreatePointer(
                    context.TimeProvider.GetUtcNow(),
                    approved.Created.SessionId,
                    0)));
        Assert.Equal(0, context.Manager.ActiveSessionCount);
    }

    [Fact]
    public void PresenterDisconnect_RevokesCredentialAndClearsConnectedState()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        var credential = approved.Approval.PresenterCredential;

        var disconnected = Assert.IsType<ConnectionDisconnectResult>(
            context.Manager.Disconnect(approved.PresenterConnectionId));

        Assert.Equal(ClientRole.Presenter, disconnected.DisconnectedRole);
        Assert.Equal("receiver-connection", disconnected.ReceiverConnectionId);
        Assert.False(disconnected.State!.Approved);
        Assert.Empty(disconnected.State.ConnectedPresenters!);
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.ResumeSession(
                "presenter-reconnected",
                new SessionResumeRequest(
                    credential.SessionId,
                    credential.Role,
                    credential.ClientInstanceId,
                    credential.SessionToken,
                    credential.ReconnectToken)));
    }

    [Fact]
    public void ReceiverDisconnect_RevokesPresentersAndResumeRequiresNewRequest()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);

        var disconnected = Assert.IsType<ConnectionDisconnectResult>(
            context.Manager.Disconnect("receiver-connection"));

        Assert.Equal(ClientRole.Receiver, disconnected.DisconnectedRole);
        Assert.Contains(
            approved.PresenterConnectionId,
            disconnected.PresenterConnectionIdsToEnd);
        Assert.False(disconnected.State!.Approved);
        Assert.Empty(context.Manager.GetAvailableReceivers());
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.ResumeSession(
                "presenter-reconnected",
                new SessionResumeRequest(
                    approved.Approval.PresenterCredential.SessionId,
                    approved.Approval.PresenterCredential.Role,
                    approved.Approval.PresenterCredential.ClientInstanceId,
                    approved.Approval.PresenterCredential.SessionToken,
                    approved.Approval.PresenterCredential.ReconnectToken)));

        var resumedReceiver = context.Manager.ResumeSession(
            "receiver-reconnected",
            new SessionResumeRequest(
                approved.Created.Credential.SessionId,
                approved.Created.Credential.Role,
                approved.Created.Credential.ClientInstanceId,
                approved.Created.Credential.SessionToken,
                approved.Created.Credential.ReconnectToken));
        var freshRequest = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(
                approved.Created.SessionId,
                "new-presenter-client",
                "1.0.0"),
            "new-presenter-connection",
            "New Presenter");

        Assert.False(resumedReceiver.State.Approved);
        Assert.True(freshRequest.Response.Accepted);
    }

    [Fact]
    public void PendingPresenterDisconnect_CancelsRequestAndAllowsReplacement()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);
        var pending = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "presenter-client", "1.0.0"),
            "pending-connection",
            "Pending Presenter");

        var disconnected = Assert.IsType<ConnectionDisconnectResult>(
            context.Manager.Disconnect("pending-connection"));
        var replacement = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "replacement-client", "1.0.0"),
            "replacement-connection",
            "Replacement Presenter");

        Assert.True(pending.Response.Accepted);
        Assert.Equal(
            "pending-connection",
            disconnected.CancelledPresenterRequestConnectionId);
        Assert.True(replacement.Response.Accepted);
    }

    [Fact]
    public void ReceiverResume_UpdatesApplicationInstanceUsedForSelfFiltering()
    {
        var context = CreateContext();
        var created = context.Manager.CreateReceiverSession(
            CreateDisplay(),
            "receiver-connection",
            "receiver-client",
            "Receiver",
            "old-application");
        context.Manager.Disconnect("receiver-connection");

        _ = context.Manager.ResumeSession(
            "receiver-reconnected",
            new SessionResumeRequest(
                created.Credential.SessionId,
                created.Credential.Role,
                created.Credential.ClientInstanceId,
                created.Credential.SessionToken,
                created.Credential.ReconnectToken),
            "new-application");

        Assert.Empty(context.Manager.GetAvailableReceivers("new-application"));
        Assert.Single(context.Manager.GetAvailableReceivers("old-application"));
    }

    [Fact]
    public void ReceiverAcknowledgement_RelaysOnlyToApprovedPresenter()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        var pointer = CreatePointer(InitialTime, approved.Created.SessionId, 1);
        _ = context.Manager.AcceptPointer(approved.PresenterConnectionId, pointer);
        var acknowledgement = new PointerAcknowledgement(pointer.EventId, 1000);

        var result = context.Manager.AcceptAcknowledgement(
            "receiver-connection",
            acknowledgement);

        Assert.Equal(approved.PresenterConnectionId, result.PresenterConnectionId);
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.AcceptAcknowledgement(
                approved.PresenterConnectionId,
                acknowledgement));
    }

    [Fact]
    public void MultiplePresenters_HonorLimitAndRouteSequencesAndAcknowledgementsIndependently()
    {
        var context = CreateContext();
        var created = context.Manager.CreateReceiverSession(
            CreateDisplay(),
            "receiver-connection",
            "receiver-client",
            "Receiver",
            maximumPresenterConnections: 2);
        var firstJoin = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "presenter-one", "1.0.0"),
            "presenter-one-connection",
            "Presenter One");
        _ = context.Manager.ApprovePresenter(
            created.SessionId,
            firstJoin.Presenter!.ConnectionId,
            "receiver-connection");

        Assert.Single(context.Manager.GetAvailableReceivers());
        var secondJoin = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "presenter-two", "1.0.0"),
            "presenter-two-connection",
            "Presenter Two");
        var secondApproval = context.Manager.ApprovePresenter(
            created.SessionId,
            secondJoin.Presenter!.ConnectionId,
            "receiver-connection");

        Assert.Equal(2, secondApproval.State.ConnectedPresenters?.Length);
        Assert.Empty(context.Manager.GetAvailableReceivers());
        var rejectedThird = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "presenter-three", "1.0.0"),
            "presenter-three-connection",
            "Presenter Three");
        Assert.False(rejectedThird.Response.Accepted);
        Assert.Contains("limit", rejectedThird.Response.Reason, StringComparison.OrdinalIgnoreCase);

        var firstPointer = CreatePointer(InitialTime, created.SessionId, 7);
        var secondPointer = CreatePointer(InitialTime, created.SessionId, 7);
        Assert.Equal(
            PointerRelayDisposition.Accepted,
            context.Manager.AcceptPointer("presenter-one-connection", firstPointer).Disposition);
        Assert.Equal(
            PointerRelayDisposition.Accepted,
            context.Manager.AcceptPointer("presenter-two-connection", secondPointer).Disposition);
        Assert.Equal(
            "presenter-one-connection",
            context.Manager.AcceptAcknowledgement(
                "receiver-connection",
                new PointerAcknowledgement(firstPointer.EventId, 1000)).PresenterConnectionId);
        Assert.Equal(
            "presenter-two-connection",
            context.Manager.AcceptAcknowledgement(
                "receiver-connection",
                new PointerAcknowledgement(secondPointer.EventId, 1001)).PresenterConnectionId);

        var firstEnded = context.Manager.EndSession(
            created.SessionId,
            "presenter-one-connection");
        Assert.True(firstEnded.ReceiverPreserved);
        Assert.Equal(
            ["Presenter Two"],
            firstEnded.State!.ConnectedPresenters!
                .Select(presenter => presenter.DisplayName)
                .ToArray());
        Assert.Single(context.Manager.GetAvailableReceivers());
    }

    [Fact]
    public void PresenterEnd_PreservesAvailableReceiverForAnotherRequest()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);

        var result = context.Manager.EndSession(
            approved.Created.SessionId,
            approved.PresenterConnectionId);

        Assert.Equal(approved.Created.SessionId, result.SessionId);
        Assert.True(result.ReceiverPreserved);
        Assert.DoesNotContain("receiver-connection", result.ConnectionIds);
        Assert.Contains(approved.PresenterConnectionId, result.ConnectionIds);
        Assert.Equal(1, context.Manager.ActiveSessionCount);
        Assert.Equal(
            approved.Created.SessionId,
            Assert.Single(context.Manager.GetAvailableReceivers()).SessionId);
        var nextJoin = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(approved.Created.SessionId, "next-presenter", "1.0.0"),
            "next-presenter-connection",
            "Next Presenter");
        Assert.True(nextJoin.Response.Accepted);
    }

    [Fact]
    public void PendingPresenterEnd_WithdrawsRequestAndReleasesTheReceiver()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);
        var join = JoinPresenter(context, created);

        var result = context.Manager.EndSession(created.SessionId, "presenter-connection");

        Assert.True(result.ReceiverPreserved);
        Assert.Equal("presenter-connection", result.CancelledPresenterRequestConnectionId);
        Assert.Contains("presenter-connection", result.ConnectionIds);
        Assert.False(result.State!.Approved);
        Assert.Equal(1, context.Manager.ActiveSessionCount);
        Assert.Equal(
            created.SessionId,
            Assert.Single(context.Manager.GetAvailableReceivers()).SessionId);
        Assert.Equal("presenter-connection", join.Presenter!.ConnectionId);
        var nextJoin = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "next-presenter", "1.0.0"),
            "next-presenter-connection",
            "Next Presenter");
        Assert.True(nextJoin.Response.Accepted);
    }

    [Fact]
    public void PendingPresenterEnd_CannotEndTheReceiverSession()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);
        _ = JoinPresenter(context, created);

        context.Manager.EndSession(created.SessionId, "presenter-connection");

        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.EndSession(created.SessionId, "presenter-connection"));
        Assert.Equal(1, context.Manager.ActiveSessionCount);
    }

    [Fact]
    public void ReceiverEnd_RemovesReceiverAndPresenter()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);

        var result = context.Manager.EndSession(
            approved.Created.SessionId,
            "receiver-connection");

        Assert.False(result.ReceiverPreserved);
        Assert.Contains("receiver-connection", result.ConnectionIds);
        Assert.Contains(approved.PresenterConnectionId, result.ConnectionIds);
        Assert.Equal(0, context.Manager.ActiveSessionCount);
    }

    [Fact]
    public void ReceiverDisconnectPresenters_PreservesAvailability()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);

        var result = context.Manager.DisconnectPresenters(
            approved.Created.SessionId,
            "receiver-connection");

        Assert.True(result.ReceiverPreserved);
        Assert.Contains(approved.PresenterConnectionId, result.ConnectionIds);
        Assert.Equal(1, context.Manager.ActiveSessionCount);
        Assert.Equal(
            approved.Created.SessionId,
            Assert.Single(context.Manager.GetAvailableReceivers()).SessionId);
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.AcceptPointer(
                approved.PresenterConnectionId,
                CreatePointer(InitialTime, approved.Created.SessionId, 1)));
    }

    [Fact]
    public void PresenterThatChangedItsPassword_StillReportsTheGroupItsSessionIsListedIn()
    {
        var context = CreateContext(requireServerPassword: true);
        context.Manager.SetConnectionGroup("receiver-connection", "shared-key");
        context.Manager.SetConnectionGroup("presenter-connection", "shared-key");
        var approved = CreateApprovedSession(context);

        // An approved presenter keeps its place when it changes its own password, so from here
        // its connection group and the group its session is published in disagree.
        context.Manager.SetConnectionGroup("presenter-connection", "private-key");
        var ended = context.Manager.EndSession(
            approved.Created.SessionId,
            "presenter-connection");

        Assert.Equal("private-key", context.Manager.GetConnectionGroup("presenter-connection"));
        Assert.Equal("shared-key", ended.GroupKey);
        Assert.True(ended.ReceiverPreserved);
    }

    [Fact]
    public void CollectedSession_ReportsTheGroupThatHasToRereadTheDirectory()
    {
        var context = CreateContext(requireServerPassword: true);
        context.Manager.SetConnectionGroup("receiver-connection", "shared-key");
        _ = CreateReceiver(context);

        context.TimeProvider.Advance(TimeSpan.FromHours(9));
        var collected = Assert.Single(context.Manager.CollectExpiredSessions());

        Assert.Equal("shared-key", collected.GroupKey);
        Assert.Equal(0, context.Manager.ActiveSessionCount);
    }

    private static TestContext CreateContext(
        int maximumPresentersPerReceiver = 16,
        bool requireServerPassword = false)
    {
        var timeProvider = new ManualTimeProvider(InitialTime);
        var manager = new SessionManager(
            Options.Create(new SessionOptions
            {
                AbandonedSessionLifetimeMinutes = 10,
                MaximumSessionHours = 8,
                SequenceWindowSize = 64,
                MaximumPresentersPerReceiver = maximumPresentersPerReceiver,
                RequireServerPassword = requireServerPassword,
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

    private static CreateSessionResponse CreateReceiver(TestContext context) =>
        context.Manager.CreateReceiverSession(
            CreateDisplay(),
            "receiver-connection",
            "receiver-client",
            "Receiver Machine");

    private static JoinSessionResult JoinPresenter(
        TestContext context,
        CreateSessionResponse created) =>
        context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "presenter-client", "1.0.0"),
            "presenter-connection",
            "Presenter Machine");

    private static void ApproveDirectPresenter(
        TestContext context,
        CreateSessionResponse created,
        string clientInstanceId)
    {
        var join = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, clientInstanceId, "1.0.0"),
            $"{clientInstanceId}-connection",
            clientInstanceId);
        _ = context.Manager.ApprovePresenter(
            created.SessionId,
            join.Presenter!.ConnectionId,
            "receiver-connection");
    }

    private static ApprovedContext CreateApprovedSession(TestContext context)
    {
        var created = CreateReceiver(context);
        var join = JoinPresenter(context, created);
        var approval = context.Manager.ApprovePresenter(
            created.SessionId,
            join.Presenter!.ConnectionId,
            "receiver-connection");
        return new ApprovedContext(
            created,
            approval,
            join.Presenter.ConnectionId);
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
        ApprovePresenterResult Approval,
        string PresenterConnectionId);
}
