using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Serialization;
using RemotePointer.Server.Hubs;
using RemotePointer.Server.RateLimiting;

namespace RemotePointer.IntegrationTests;

public sealed class PointerHubIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Discovery_DirectJoinAndPresenterDisconnect_PreserveReceiverAvailability()
    {
        using var factory = CreateFactory(receiverDiscoveryEnabled: true);
        await using var receiver = CreateConnection(factory, "receiver-client", "Receiver Machine");
        await using var presenter = CreateConnection(factory, "presenter-client", "Presenter Machine");
        var joinRequested = CompletionSource<PresenterDescriptor>();
        var approved = CompletionSource<SessionStateMessage>();
        var availableAgain = CompletionSource<SessionStateMessage>();
        var presenterDisconnected = CompletionSource<string>();
        var displayChanged = CompletionSource<DisplayDescriptor>();
        receiver.On<PresenterDescriptor>("PresenterJoinRequested", joinRequested.SetResult);
        receiver.On<SessionStateMessage>(
            "SessionApproved",
            state =>
            {
                if (!state.Approved)
                {
                    availableAgain.TrySetResult(state);
                }
            });
        presenter.On<SessionStateMessage>("SessionApproved", approved.SetResult);
        presenter.On<string>("SessionEnded", presenterDisconnected.SetResult);
        presenter.On<DisplayDescriptor>("ReceiverDisplayChanged", displayChanged.SetResult);
        await receiver.StartAsync();
        await presenter.StartAsync();

        var capabilities = await presenter.InvokeAsync<RelayCapabilities>("GetRelayCapabilities");
        Assert.True(capabilities.ReceiverDiscoveryEnabled);
        var created = await receiver.InvokeAsync<CreateSessionResponse>(
            "CreateReceiverSession",
            CreateDisplay());
        var listed = Assert.Single(
            await presenter.InvokeAsync<AvailableReceiverDescriptor[]>("GetAvailableReceivers"));
        Assert.Equal("Receiver Machine", listed.DisplayName);

        var join = await presenter.InvokeAsync<JoinResponse>(
            "RequestToJoinReceiver",
            new DirectJoinRequest(created.SessionId, "presenter-client", "1.0.0"));
        var pending = await joinRequested.Task.WaitAsync(TestTimeout);
        Assert.True(join.Accepted);
        Assert.False(approved.Task.IsCompleted);

        await receiver.InvokeAsync("ApprovePresenter", created.SessionId, pending.ConnectionId);
        _ = await approved.Task.WaitAsync(TestTimeout);
        var updatedDisplay = new DisplayDescriptor(
            "display-1",
            "Display 1",
            1_200,
            1_920,
            1d,
            90);
        await receiver.InvokeAsync("UpdateReceiverDisplay", created.SessionId, updatedDisplay);
        Assert.Equal(updatedDisplay, await displayChanged.Task.WaitAsync(TestTimeout));

        await presenter.InvokeAsync("EndSession", created.SessionId);
        Assert.Contains(
            "Disconnected",
            await presenterDisconnected.Task.WaitAsync(TestTimeout),
            StringComparison.OrdinalIgnoreCase);
        Assert.True((await availableAgain.Task.WaitAsync(TestTimeout)).ReceiverDiscoverable);
        Assert.Equal(
            created.SessionId,
            Assert.Single(
                await presenter.InvokeAsync<AvailableReceiverDescriptor[]>(
                    "GetAvailableReceivers")).SessionId);
        var nextJoin = await presenter.InvokeAsync<JoinResponse>(
            "RequestToJoinReceiver",
            new DirectJoinRequest(created.SessionId, "presenter-client", "1.0.0"));
        Assert.True(nextJoin.Accepted);
    }

    [Fact]
    public async Task ApprovedSession_RelaysAcknowledgesReconnectsAndTerminates()
    {
        using var factory = CreateFactory();
        await using var receiver = CreateConnection(factory, "receiver-client", "Receiver Machine");
        await using var presenter = CreateConnection(factory, "presenter-client", "Presenter Machine");
        var joinRequested = CompletionSource<PresenterDescriptor>();
        var presenterCredential = CompletionSource<SessionCredential>();
        var firstPointerReceived = CompletionSource<PointerEventMessage>();
        var acknowledgementReceived = CompletionSource<PointerAcknowledgement>();
        receiver.On<PresenterDescriptor>("PresenterJoinRequested", joinRequested.SetResult);
        receiver.On<PointerEventMessage>("PointerReceived", firstPointerReceived.SetResult);
        presenter.On<SessionCredential>("SessionCredentialIssued", presenterCredential.SetResult);
        presenter.On<PointerAcknowledgement>("PointerDisplayed", acknowledgementReceived.SetResult);

        await receiver.StartAsync();
        await presenter.StartAsync();

        var created = await receiver.InvokeAsync<CreateSessionResponse>(
            "CreateReceiverSession",
            CreateDisplay());
        var joinResponse = await presenter.InvokeAsync<JoinResponse>(
            "RequestToJoinSession",
            new JoinRequest(
                created.PairingCode,
                ClientRole.Presenter,
                "presenter-client",
                "1.0.0"));
        var presenterDescriptor = await joinRequested.Task.WaitAsync(TestTimeout);

        Assert.True(joinResponse.Accepted);
        Assert.Equal(created.SessionId, joinResponse.SessionId);
        Assert.Equal("Presenter Machine", presenterDescriptor.DisplayName);

        await receiver.InvokeAsync(
            "ApprovePresenter",
            created.SessionId,
            presenterDescriptor.ConnectionId);
        var issuedPresenterCredential = await presenterCredential.Task.WaitAsync(TestTimeout);
        var firstPointer = CreatePointer(created.SessionId, sequenceNumber: 0);

        await presenter.InvokeAsync("SendPointer", firstPointer);
        var received = await firstPointerReceived.Task.WaitAsync(TestTimeout);
        Assert.Equal(firstPointer, received);

        var acknowledgement = new PointerAcknowledgement(
            firstPointer.EventId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await receiver.InvokeAsync("AcknowledgePointer", acknowledgement);
        Assert.Equal(
            acknowledgement,
            await acknowledgementReceived.Task.WaitAsync(TestTimeout));

        await presenter.StopAsync();
        await using var resumedPresenter = CreateConnection(
            factory,
            "presenter-client",
            "Presenter Machine");
        await resumedPresenter.StartAsync();
        var rotatedPresenterCredential = await resumedPresenter.InvokeAsync<SessionCredential>(
            "ResumeSession",
            new SessionResumeRequest(
                issuedPresenterCredential.SessionId,
                ClientRole.Presenter,
                issuedPresenterCredential.ClientInstanceId,
                issuedPresenterCredential.SessionToken,
                issuedPresenterCredential.ReconnectToken));
        Assert.NotEqual(
            issuedPresenterCredential.ReconnectToken,
            rotatedPresenterCredential.ReconnectToken);

        await receiver.StopAsync();
        await using var resumedReceiver = CreateConnection(
            factory,
            "receiver-client",
            "Receiver Machine");
        var secondPointerReceived = CompletionSource<PointerEventMessage>();
        var receiverSessionEnded = CompletionSource<string>();
        var presenterSessionEnded = CompletionSource<string>();
        resumedReceiver.On<PointerEventMessage>("PointerReceived", secondPointerReceived.SetResult);
        resumedReceiver.On<string>("SessionEnded", receiverSessionEnded.SetResult);
        resumedPresenter.On<string>("SessionEnded", presenterSessionEnded.SetResult);
        await resumedReceiver.StartAsync();
        var rotatedReceiverCredential = await resumedReceiver.InvokeAsync<SessionCredential>(
            "ResumeSession",
            new SessionResumeRequest(
                created.Credential.SessionId,
                ClientRole.Receiver,
                created.Credential.ClientInstanceId,
                created.Credential.SessionToken,
                created.Credential.ReconnectToken));
        Assert.NotEqual(created.Credential.ReconnectToken, rotatedReceiverCredential.ReconnectToken);

        var secondPointer = CreatePointer(created.SessionId, sequenceNumber: 1) with
        {
            Kind = PointerKind.RectangleStart,
            GestureId = Guid.NewGuid(),
        };
        await resumedPresenter.InvokeAsync("SendPointer", secondPointer);
        Assert.Equal(
            secondPointer,
            await secondPointerReceived.Task.WaitAsync(TestTimeout));

        await resumedReceiver.InvokeAsync("EndSession", created.SessionId);
        Assert.Contains(
            "ended",
            await receiverSessionEnded.Task.WaitAsync(TestTimeout),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ended",
            await presenterSessionEnded.Task.WaitAsync(TestTimeout),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnauthorizedClient_CannotSendPointer()
    {
        using var factory = CreateFactory();
        await using var unauthorized = CreateConnection(factory, "third-client", "Third Client");
        await unauthorized.StartAsync();

        await Assert.ThrowsAsync<HubException>(
            () => unauthorized.InvokeAsync(
                "SendPointer",
                CreatePointer("unknown-session", sequenceNumber: 0)));
    }

    [Fact]
    public async Task OversizedHubInvocation_IsRejectedByTransport()
    {
        using var factory = CreateFactory();
        await using var connection = CreateConnection(factory, "large-client", "Large Client");
        await connection.StartAsync();
        var oversizedRequest = new JoinRequest(
            "AB2D4E",
            ClientRole.Presenter,
            "large-client",
            new string('x', 40_000));

        var exception = await Record.ExceptionAsync(
            () => connection.InvokeAsync("RequestToJoinSession", oversizedRequest)
                .WaitAsync(TestTimeout));

        Assert.NotNull(exception);
    }

    [Fact]
    public async Task HealthEndpointAndMessageSizeLimit_AreConfigured()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");
        var hubOptions = factory.Services.GetRequiredService<IOptions<HubOptions>>().Value;
        var rateLimitOptions = factory.Services
            .GetRequiredService<IOptions<PointerRateLimitOptions>>()
            .Value;

        response.EnsureSuccessStatusCode();
        Assert.Equal(32 * 1024, hubOptions.MaximumReceiveMessageSize);
        Assert.Equal(1, hubOptions.MaximumParallelInvocationsPerClient);
        Assert.Equal(90, rateLimitOptions.EventsPerSecond);
        Assert.Equal(180, rateLimitOptions.BurstSize);
    }

    [Fact]
    public async Task Production_RejectsPlaintextWithoutRedirectAndAddsHstsToHttps()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var plaintextClient = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("http://localhost"),
            });

        using var plaintextResponse = await plaintextClient.GetAsync("/health");

        Assert.Equal(HttpStatusCode.BadRequest, plaintextResponse.StatusCode);
        Assert.Null(plaintextResponse.Headers.Location);

        using var secureClient = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://pointer.example.test"),
            });
        using var secureResponse = await secureClient.GetAsync("/health");

        secureResponse.EnsureSuccessStatusCode();
        Assert.True(secureResponse.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Production_AllowsPrivateHttpOnlyWhenHttpsProxyModeIsExplicitlyEnabled()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(
                builder =>
                {
                    builder.UseEnvironment("Production");
                    builder.UseSetting("Deployment:BehindHttpsProxy", "true");
                });
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("http://relay"),
            });

        using var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task HubRateLimit_RejectsThirtyFirstImmediatePointer()
    {
        using var factory = CreateFactory().WithWebHostBuilder(
            builder =>
            {
                builder.UseSetting("RateLimits:EventsPerSecond", "20");
                builder.UseSetting("RateLimits:BurstSize", "30");
            });
        await using var receiver = CreateConnection(factory, "receiver-rate", "Receiver");
        await using var presenter = CreateConnection(factory, "presenter-rate", "Presenter");
        var joinRequested = CompletionSource<PresenterDescriptor>();
        var presenterCredential = CompletionSource<SessionCredential>();
        var allPointersReceived = CompletionSource<int>();
        var receivedCount = 0;
        receiver.On<PresenterDescriptor>("PresenterJoinRequested", joinRequested.SetResult);
        receiver.On<PointerEventMessage>(
            "PointerReceived",
            _ =>
            {
                var count = Interlocked.Increment(ref receivedCount);
                if (count == 30)
                {
                    allPointersReceived.TrySetResult(count);
                }
            });
        presenter.On<SessionCredential>("SessionCredentialIssued", presenterCredential.SetResult);
        await receiver.StartAsync();
        await presenter.StartAsync();
        var created = await receiver.InvokeAsync<CreateSessionResponse>(
            "CreateReceiverSession",
            CreateDisplay());
        _ = await presenter.InvokeAsync<JoinResponse>(
            "RequestToJoinSession",
            new JoinRequest(
                created.PairingCode,
                ClientRole.Presenter,
                "presenter-rate",
                "1.0.0"));
        var pending = await joinRequested.Task.WaitAsync(TestTimeout);
        await receiver.InvokeAsync("ApprovePresenter", created.SessionId, pending.ConnectionId);
        _ = await presenterCredential.Task.WaitAsync(TestTimeout);

        for (var sequence = 0; sequence < 30; sequence++)
        {
            await presenter.InvokeAsync(
                "SendPointer",
                CreatePointer(created.SessionId, sequence));
        }

        await Assert.ThrowsAsync<HubException>(
            () => presenter.InvokeAsync(
                "SendPointer",
                CreatePointer(created.SessionId, 30)));
        Assert.Equal(30, await allPointersReceived.Task.WaitAsync(TestTimeout));
    }

    private static WebApplicationFactory<Program> CreateFactory(
        bool receiverDiscoveryEnabled = false) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(
                builder =>
                {
                    builder.UseEnvironment("Development");
                    builder.UseSetting(
                        "Sessions:ReceiverDiscoveryEnabled",
                        receiverDiscoveryEnabled.ToString());
                });

    private static HubConnection CreateConnection(
        WebApplicationFactory<Program> factory,
        string clientInstanceId,
        string displayName)
    {
        var server = factory.Server;
        var query = $"?clientInstanceId={Uri.EscapeDataString(clientInstanceId)}"
            + $"&displayName={Uri.EscapeDataString(displayName)}";
        var url = new Uri(server.BaseAddress, $"/hubs/pointer{query}");
        return new HubConnectionBuilder()
            .WithUrl(
                url,
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                })
            .AddJsonProtocol(
                options => RemotePointerJson.Configure(options.PayloadSerializerOptions))
            .Build();
    }

    private static TaskCompletionSource<T> CompletionSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static DisplayDescriptor CreateDisplay() => new(
        "display-1",
        "Display 1",
        1_920,
        1_080,
        1d,
        0);

    private static PointerEventMessage CreatePointer(string sessionId, long sequenceNumber) => new(
        Guid.NewGuid(),
        sessionId,
        sequenceNumber,
        0.25d,
        0.75d,
        PointerKind.Click,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        2_000);
}
