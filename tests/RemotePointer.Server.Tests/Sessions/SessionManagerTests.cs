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
    public void DiscoverableReceiver_CanReceiveDirectJoinRequestButStillRequiresApproval()
    {
        var context = CreateContext(receiverDiscoveryEnabled: true);
        var created = CreateReceiver(context);
        Assert.True(context.Manager.SetReceiverDiscoverable(
            created.SessionId,
            "receiver-connection",
            true));

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
    public void DiscoverableReceiver_RemainsAvailableAfterPairingCodeExpires()
    {
        var context = CreateContext(receiverDiscoveryEnabled: true);
        var created = CreateReceiver(context);
        _ = context.Manager.SetReceiverDiscoverable(
            created.SessionId,
            "receiver-connection",
            true);
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
    public void ReceiverAcknowledgement_RelaysOnlyToApprovedPresenter()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);
        var acknowledgement = new PointerAcknowledgement(Guid.NewGuid(), 1000);

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
    public void EitherApprovedParticipant_CanEndSession()
    {
        var context = CreateContext();
        var approved = CreateApprovedSession(context);

        var result = context.Manager.EndSession(
            approved.Created.SessionId,
            approved.PresenterConnectionId);

        Assert.Equal(approved.Created.SessionId, result.SessionId);
        Assert.Contains("receiver-connection", result.ConnectionIds);
        Assert.Contains(approved.PresenterConnectionId, result.ConnectionIds);
        Assert.Equal(0, context.Manager.ActiveSessionCount);
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
