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
    public void ReceiverDiscovery_IsDisabledByDefault()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);

        Assert.False(context.Manager.ReceiverDiscoveryEnabled);
        Assert.Empty(context.Manager.GetAvailableReceivers());
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.SetReceiverDiscoverable(
                created.SessionId,
                "receiver-connection",
                true));
        var join = context.Manager.RequestToJoinReceiver(
            new DirectJoinRequest(created.SessionId, "presenter-client", "1.0.0"),
            "presenter-connection",
            "Presenter Machine");
        Assert.False(join.Response.Accepted);
    }

    [Fact]
    public void Receiver_IsAutomaticallyDiscoverableAndDirectJoinStillRequiresApproval()
    {
        var context = CreateContext(receiverDiscoveryEnabled: true);
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
    public void Discovery_ExcludesOnlySameApplicationInstanceAndIncludesProfilePicture()
    {
        var context = CreateContext(receiverDiscoveryEnabled: true);
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
        var context = CreateContext(receiverDiscoveryEnabled: true);
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
        var context = CreateContext(receiverDiscoveryEnabled: true);
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
    public void DiscoverableReceiver_RemainsAvailableAfterPairingCodeExpires()
    {
        var context = CreateContext(receiverDiscoveryEnabled: true);
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
    public void CreateReceiverSession_IssuesReceiverOnlyCredentialAndPairingExpiry()
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
        Assert.Equal(InitialTime.AddMinutes(10), response.PairingCodeExpiresAt);
        Assert.Equal(InitialTime.AddHours(8), response.Credential.ExpiresAt);
        Assert.Equal(1, context.Manager.ActiveSessionCount);
    }

    [Fact]
    public void RequestToJoinSession_ConsumesCodeAndExposesNoSessionDataToRejectedClient()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);

        var first = context.Manager.RequestToJoinSession(
            CreateJoin(created.PairingCode, "presenter-one"),
            "presenter-connection-one",
            "Presenter One");
        var second = context.Manager.RequestToJoinSession(
            CreateJoin(created.PairingCode, "presenter-two"),
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
    public void PairingCodeExpiry_RejectsJoinAndRemovesSession()
    {
        var context = CreateContext();
        var created = CreateReceiver(context);
        context.TimeProvider.Advance(TimeSpan.FromMinutes(11));

        var result = context.Manager.RequestToJoinSession(
            CreateJoin(created.PairingCode, "presenter-client"),
            "presenter-connection",
            "Presenter");

        Assert.False(result.Response.Accepted);
        Assert.Null(result.Response.SessionId);
        Assert.Equal(0, context.Manager.ActiveSessionCount);
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
    public void ResumeSession_ValidatesAndRotatesReconnectToken()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        context.Manager.Disconnect(approved.PresenterConnectionId);
        var credential = approved.Approval.PresenterCredential;

        var resumed = context.Manager.ResumeSession(
            "presenter-reconnected",
            new SessionResumeRequest(
                credential.SessionId,
                credential.Role,
                credential.ClientInstanceId,
                credential.SessionToken,
                credential.ReconnectToken));

        Assert.NotEqual(credential.ReconnectToken, resumed.Credential.ReconnectToken);
        Assert.Single(resumed.State.ConnectedPresenters!);
        context.Manager.Disconnect("presenter-reconnected");
        Assert.ThrowsAny<InvalidOperationException>(
            () => context.Manager.ResumeSession(
                "presenter-replay",
                new SessionResumeRequest(
                    credential.SessionId,
                    credential.Role,
                    credential.ClientInstanceId,
                    credential.SessionToken,
                    credential.ReconnectToken)));
    }

    [Fact]
    public void ReceiverResume_UpdatesApplicationInstanceUsedForSelfFiltering()
    {
        var context = CreateContext(receiverDiscoveryEnabled: true);
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
        var context = CreateContext(receiverDiscoveryEnabled: true);
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
        var context = CreateContext(receiverDiscoveryEnabled: true);
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
    public void ReceiverEnd_RemovesReceiverAndPresenter()
    {
        var context = CreateContext(receiverDiscoveryEnabled: true);
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
        var context = CreateContext(receiverDiscoveryEnabled: true);
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

    private static TestContext CreateContext(bool receiverDiscoveryEnabled = false)
    {
        var timeProvider = new ManualTimeProvider(InitialTime);
        var manager = new SessionManager(
            Options.Create(new SessionOptions
            {
                PairingCodeLifetimeMinutes = 10,
                MaximumSessionHours = 8,
                SequenceWindowSize = 64,
                ReceiverDiscoveryEnabled = receiverDiscoveryEnabled,
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
        context.Manager.RequestToJoinSession(
            CreateJoin(created.PairingCode, "presenter-client"),
            "presenter-connection",
            "Presenter Machine");

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

    private static JoinRequest CreateJoin(string code, string clientInstanceId) => new(
        code,
        ClientRole.Presenter,
        clientInstanceId,
        "1.0.0");

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
