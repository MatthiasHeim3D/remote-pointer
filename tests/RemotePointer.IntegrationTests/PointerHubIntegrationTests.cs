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
    public async Task Discovery_AvailabilityChangesNotifyConnectedClients()
    {
        using var factory = CreateFactory();
        await using var host = CreateConnection(factory, "host-notify", "Host");
        await using var observer = CreateConnection(factory, "observer-notify", "Observer");
        var notificationCount = 0;
        var receivedBoth = CompletionSource<bool>();
        observer.On(
            "HostDirectoryChanged",
            () =>
            {
                if (Interlocked.Increment(ref notificationCount) >= 2)
                {
                    receivedBoth.TrySetResult(true);
                }
            });
        await host.StartAsync();
        await observer.StartAsync();

        var created = await host.InvokeAsync<CreateSessionResponse>(
            "CreateHostSession",
            CreateDisplay(),
            new ClientProfile(),
            2,
            string.Empty);
        await host.InvokeAsync<bool>(
            "SetHostDiscoverable",
            created.SessionId,
            false);

        Assert.True(await receivedBoth.Task.WaitAsync(TestTimeout));
        Assert.Empty(await observer.InvokeAsync<AvailableHostDescriptor[]>(
            "GetAvailableHosts"));
    }

    [Fact]
    public async Task Host_AcceptsMultipleAnnotatorsUpToConfiguredLimit()
    {
        using var factory = CreateFactory();
        await using var host = CreateConnection(factory, "host-multi", "Host");
        await using var firstAnnotator = CreateConnection(factory, "annotator-one", "Annotator One");
        await using var secondAnnotator = CreateConnection(factory, "annotator-two", "Annotator Two");
        await using var thirdAnnotator = CreateConnection(factory, "annotator-three", "Annotator Three");
        var firstPending = CompletionSource<AnnotatorDescriptor>();
        var secondPending = CompletionSource<AnnotatorDescriptor>();
        var connectedState = CompletionSource<SessionStateMessage>();
        var firstEnded = CompletionSource<string>();
        var secondEnded = CompletionSource<string>();
        var requestCount = 0;
        host.On<AnnotatorDescriptor>(
            "AnnotatorJoinRequested",
            annotator =>
            {
                if (Interlocked.Increment(ref requestCount) == 1)
                {
                    firstPending.TrySetResult(annotator);
                }
                else
                {
                    secondPending.TrySetResult(annotator);
                }
            });
        host.On<SessionStateMessage>(
            "SessionApproved",
            state =>
            {
                if (state.ConnectedAnnotators?.Length == 2)
                {
                    connectedState.TrySetResult(state);
                }
            });
        firstAnnotator.On<string>("SessionEnded", firstEnded.SetResult);
        secondAnnotator.On<string>("SessionEnded", secondEnded.SetResult);
        await host.StartAsync();
        await firstAnnotator.StartAsync();
        await secondAnnotator.StartAsync();
        await thirdAnnotator.StartAsync();
        var created = await host.InvokeAsync<CreateSessionResponse>(
            "CreateHostSession",
            CreateDisplay(),
            new ClientProfile(),
            2,
            string.Empty);

        Assert.True((await firstAnnotator.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "annotator-one", "1.0.0"), string.Empty)).Accepted);
        var first = await firstPending.Task.WaitAsync(TestTimeout);
        await host.InvokeAsync("ApproveAnnotator", created.SessionId, first.ConnectionId);

        Assert.True((await secondAnnotator.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "annotator-two", "1.0.0"), string.Empty)).Accepted);
        var second = await secondPending.Task.WaitAsync(TestTimeout);
        await host.InvokeAsync("ApproveAnnotator", created.SessionId, second.ConnectionId);
        var state = await connectedState.Task.WaitAsync(TestTimeout);

        Assert.Equal(
            ["Annotator One", "Annotator Two"],
            state.ConnectedAnnotators!.Select(annotator => annotator.DisplayName).ToArray());
        Assert.Empty(await thirdAnnotator.InvokeAsync<AvailableHostDescriptor[]>(
            "GetAvailableHosts"));
        var thirdJoin = await thirdAnnotator.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "annotator-three", "1.0.0"), string.Empty);
        Assert.False(thirdJoin.Accepted);

        await host.InvokeAsync("DisconnectAllConnections", created.SessionId);
        Assert.Contains("host", await firstEnded.Task.WaitAsync(TestTimeout));
        Assert.Contains("host", await secondEnded.Task.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Discovery_HidesAndRejectsSelfButAllowsSameMachinePeerWithProfile()
    {
        using var factory = CreateFactory();
        await using var host = CreateConnection(
            factory,
            "shared-machine-profile",
            "Host",
            "host-application");
        await using var selfProbe = CreateConnection(
            factory,
            "shared-machine-profile",
            "Self probe",
            "host-application");
        await using var otherInstance = CreateConnection(
            factory,
            "shared-machine-profile",
            "Other instance",
            "other-application");
        await host.StartAsync();
        await selfProbe.StartAsync();
        await otherInstance.StartAsync();
        byte[] picture = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        var created = await host.InvokeAsync<CreateSessionResponse>(
            "CreateHostSession",
            CreateDisplay(),
            new ClientProfile(picture),
            2,
            string.Empty);

        Assert.Empty(
            await selfProbe.InvokeAsync<AvailableHostDescriptor[]>(
                "GetAvailableHosts"));
        var visible = Assert.Single(
            await otherInstance.InvokeAsync<AvailableHostDescriptor[]>(
                "GetAvailableHosts"));
        Assert.Equal(picture, visible.ProfilePicturePng);
        var selfJoin = await selfProbe.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "shared-machine-profile", "1.0.0"), string.Empty);
        var peerJoin = await otherInstance.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "shared-machine-profile", "1.0.0"), string.Empty);

        Assert.False(selfJoin.Accepted);
        Assert.True(peerJoin.Accepted);
    }

    [Fact]
    public async Task ActiveHost_ProfileChangePropagatesWithoutReconnect()
    {
        using var factory = CreateFactory();
        await using var host = CreateConnection(factory, "host-live-profile", "Host");
        await using var annotator = CreateConnection(factory, "annotator-live-profile", "Annotator");
        var joinRequested = CompletionSource<AnnotatorDescriptor>();
        var updatedState = CompletionSource<SessionStateMessage>();
        host.On<AnnotatorDescriptor>("AnnotatorJoinRequested", joinRequested.SetResult);
        annotator.On<SessionStateMessage>(
            "SessionApproved",
            state =>
            {
                if (state.HostDisplayName == "Updated Host")
                {
                    updatedState.TrySetResult(state);
                }
            });
        await host.StartAsync();
        await annotator.StartAsync();
        var created = await host.InvokeAsync<CreateSessionResponse>(
            "CreateHostSession",
            CreateDisplay(),
            new ClientProfile(),
            2,
            "Host");
        var join = await annotator.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "annotator-live-profile", "1.0.0"),
            "Annotator");
        Assert.True(join.Accepted);
        var pending = await joinRequested.Task.WaitAsync(TestTimeout);
        await host.InvokeAsync("ApproveAnnotator", created.SessionId, pending.ConnectionId);
        byte[] picture = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        await host.InvokeAsync(
            "UpdateHostClientSettings",
            created.SessionId,
            "Updated Host",
            new ClientProfile(picture),
            2);

        var state = await updatedState.Task.WaitAsync(TestTimeout);
        Assert.Equal(picture, state.HostProfilePicturePng);
        Assert.Equal(created.SessionId, state.SessionId);
    }

    [Fact]
    public async Task HostRejection_NotifiesPendingAnnotatorAndRestoresAvailability()
    {
        using var factory = CreateFactory();
        await using var host = CreateConnection(factory, "host-reject", "Host");
        await using var annotator = CreateConnection(factory, "annotator-reject", "Annotator");
        var joinRequested = CompletionSource<AnnotatorDescriptor>();
        var rejected = CompletionSource<string>();
        host.On<AnnotatorDescriptor>("AnnotatorJoinRequested", joinRequested.SetResult);
        annotator.On<string>("SessionEnded", rejected.SetResult);
        await host.StartAsync();
        await annotator.StartAsync();
        var created = await host.InvokeAsync<CreateSessionResponse>(
            "CreateHostSession",
            CreateDisplay(),
            new ClientProfile(),
            2,
            string.Empty);
        var join = await annotator.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "annotator-reject", "1.0.0"), string.Empty);
        Assert.True(join.Accepted);
        var pending = await joinRequested.Task.WaitAsync(TestTimeout);

        await host.InvokeAsync("RejectAnnotator", created.SessionId, pending.ConnectionId);

        Assert.Contains(
            "declined",
            await rejected.Task.WaitAsync(TestTimeout),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            created.SessionId,
            Assert.Single(
                await annotator.InvokeAsync<AvailableHostDescriptor[]>(
                    "GetAvailableHosts")).SessionId);
    }

    [Fact]
    public async Task Discovery_HostDisconnectAll_PreservesHostAvailability()
    {
        using var factory = CreateFactory();
        await using var host = CreateConnection(factory, "host-client", "Host Machine");
        await using var annotator = CreateConnection(factory, "annotator-client", "Annotator Machine");
        var joinRequested = CompletionSource<AnnotatorDescriptor>();
        var approved = CompletionSource<SessionStateMessage>();
        var availableAgain = CompletionSource<SessionStateMessage>();
        var annotatorDisconnected = CompletionSource<string>();
        var displayChanged = CompletionSource<DisplayDescriptor>();
        host.On<AnnotatorDescriptor>("AnnotatorJoinRequested", joinRequested.SetResult);
        host.On<SessionStateMessage>(
            "SessionApproved",
            state =>
            {
                if (!state.Approved)
                {
                    availableAgain.TrySetResult(state);
                }
            });
        annotator.On<SessionStateMessage>("SessionApproved", approved.SetResult);
        annotator.On<string>("SessionEnded", annotatorDisconnected.SetResult);
        annotator.On<DisplayDescriptor>("HostDisplayChanged", displayChanged.SetResult);
        await host.StartAsync();
        await annotator.StartAsync();

        var created = await host.InvokeAsync<CreateSessionResponse>(
            "CreateHostSession",
            CreateDisplay(),
            new ClientProfile(),
            2,
            string.Empty);
        var listed = Assert.Single(
            await annotator.InvokeAsync<AvailableHostDescriptor[]>("GetAvailableHosts"));
        Assert.Equal("Host Machine", listed.DisplayName);

        var join = await annotator.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "annotator-client", "1.0.0"), string.Empty);
        var pending = await joinRequested.Task.WaitAsync(TestTimeout);
        Assert.True(join.Accepted);
        Assert.False(approved.Task.IsCompleted);

        await host.InvokeAsync("ApproveAnnotator", created.SessionId, pending.ConnectionId);
        _ = await approved.Task.WaitAsync(TestTimeout);
        var updatedDisplay = new DisplayDescriptor(
            "display-1",
            "Display 1",
            1_200,
            1_920,
            1d,
            90);
        await host.InvokeAsync("UpdateHostDisplay", created.SessionId, updatedDisplay);
        Assert.Equal(updatedDisplay, await displayChanged.Task.WaitAsync(TestTimeout));

        await host.InvokeAsync("DisconnectAllConnections", created.SessionId);
        Assert.Contains(
            "host",
            await annotatorDisconnected.Task.WaitAsync(TestTimeout),
            StringComparison.OrdinalIgnoreCase);
        Assert.True((await availableAgain.Task.WaitAsync(TestTimeout)).HostDiscoverable);
        Assert.Equal(
            created.SessionId,
            Assert.Single(
                await annotator.InvokeAsync<AvailableHostDescriptor[]>(
                    "GetAvailableHosts")).SessionId);
        var nextJoin = await annotator.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "annotator-client", "1.0.0"), string.Empty);
        Assert.True(nextJoin.Accepted);
    }

    [Fact]
    public async Task ApprovedSession_RevokesPeersOnDisconnectAndRequiresFreshRequest()
    {
        using var factory = CreateFactory();
        await using var host = CreateConnection(factory, "host-client", "Host Machine");
        await using var annotator = CreateConnection(factory, "annotator-client", "Annotator Machine");
        var joinRequested = CompletionSource<AnnotatorDescriptor>();
        var annotatorCredential = CompletionSource<SessionCredential>();
        var firstPointerReceived = CompletionSource<PointerEventMessage>();
        var acknowledgementReceived = CompletionSource<PointerAcknowledgement>();
        host.On<AnnotatorDescriptor>("AnnotatorJoinRequested", joinRequested.SetResult);
        host.On<PointerEventMessage>("PointerReceived", firstPointerReceived.SetResult);
        annotator.On<SessionCredential>("SessionCredentialIssued", annotatorCredential.SetResult);
        annotator.On<PointerAcknowledgement>("PointerDisplayed", acknowledgementReceived.SetResult);

        await host.StartAsync();
        await annotator.StartAsync();

        var created = await host.InvokeAsync<CreateSessionResponse>(
            "CreateHostSession",
            CreateDisplay(),
            new ClientProfile(),
            2,
            string.Empty);
        var joinResponse = await annotator.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "annotator-client", "1.0.0"),
            string.Empty);
        var annotatorDescriptor = await joinRequested.Task.WaitAsync(TestTimeout);

        Assert.True(joinResponse.Accepted);
        Assert.Equal(created.SessionId, joinResponse.SessionId);
        Assert.Equal("Annotator Machine", annotatorDescriptor.DisplayName);

        await host.InvokeAsync(
            "ApproveAnnotator",
            created.SessionId,
            annotatorDescriptor.ConnectionId);
        var issuedAnnotatorCredential = await annotatorCredential.Task.WaitAsync(TestTimeout);
        var firstPointer = CreatePointer(created.SessionId, sequenceNumber: 0);

        await annotator.InvokeAsync("SendPointer", firstPointer);
        var received = await firstPointerReceived.Task.WaitAsync(TestTimeout);
        // The relay stamps the sending annotator onto the event it forwards.
        Assert.Equal(firstPointer with { AnnotatorId = "annotator-client" }, received);

        var acknowledgement = new PointerAcknowledgement(
            firstPointer.EventId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await host.InvokeAsync("AcknowledgePointer", acknowledgement);
        Assert.Equal(
            acknowledgement,
            await acknowledgementReceived.Task.WaitAsync(TestTimeout));

        var annotatorDisconnectedState = CompletionSource<SessionStateMessage>();
        host.On<SessionStateMessage>(
            "SessionApproved",
            state =>
            {
                if (!state.Approved)
                {
                    annotatorDisconnectedState.TrySetResult(state);
                }
            });
        await annotator.StopAsync();
        Assert.Empty(
            (await annotatorDisconnectedState.Task.WaitAsync(TestTimeout)).ConnectedAnnotators!);

        await using var resumedAnnotator = CreateConnection(
            factory,
            "annotator-client",
            "Annotator Machine");
        await resumedAnnotator.StartAsync();
        await Assert.ThrowsAsync<HubException>(
            () => resumedAnnotator.InvokeAsync<SessionCredential>(
                "ResumeSession",
                new SessionResumeRequest(
                    issuedAnnotatorCredential.SessionId,
                    ClientRole.Annotator,
                    issuedAnnotatorCredential.ClientInstanceId,
                    issuedAnnotatorCredential.SessionToken,
                    issuedAnnotatorCredential.ReconnectToken)));

        var secondJoinRequested = CompletionSource<AnnotatorDescriptor>();
        host.On<AnnotatorDescriptor>(
            "AnnotatorJoinRequested",
            secondJoinRequested.SetResult);
        var freshJoin = await resumedAnnotator.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "annotator-client", "1.0.0"), string.Empty);
        var secondAnnotatorDescriptor = await secondJoinRequested.Task.WaitAsync(TestTimeout);
        Assert.True(freshJoin.Accepted);
        await host.InvokeAsync(
            "ApproveAnnotator",
            created.SessionId,
            secondAnnotatorDescriptor.ConnectionId);

        var annotatorSessionEnded = CompletionSource<string>();
        resumedAnnotator.On<string>("SessionEnded", annotatorSessionEnded.SetResult);
        await host.StopAsync();
        Assert.Contains(
            "request access again",
            await annotatorSessionEnded.Task.WaitAsync(TestTimeout),
            StringComparison.OrdinalIgnoreCase);

        await using var resumedHost = CreateConnection(
            factory,
            "host-client",
            "Host Machine");
        var resumedHostState = CompletionSource<SessionStateMessage>();
        var hostSessionEnded = CompletionSource<string>();
        resumedHost.On<SessionStateMessage>(
            "SessionApproved",
            resumedHostState.SetResult);
        resumedHost.On<string>("SessionEnded", hostSessionEnded.SetResult);
        await resumedHost.StartAsync();
        var rotatedHostCredential = await resumedHost.InvokeAsync<SessionCredential>(
            "ResumeSession",
            new SessionResumeRequest(
                created.Credential.SessionId,
                ClientRole.Host,
                created.Credential.ClientInstanceId,
                created.Credential.SessionToken,
                created.Credential.ReconnectToken));
        Assert.NotEqual(created.Credential.ReconnectToken, rotatedHostCredential.ReconnectToken);
        Assert.False((await resumedHostState.Task.WaitAsync(TestTimeout)).Approved);

        var thirdJoinRequested = CompletionSource<AnnotatorDescriptor>();
        resumedHost.On<AnnotatorDescriptor>(
            "AnnotatorJoinRequested",
            thirdJoinRequested.SetResult);
        var requestAfterHostRestart = await resumedAnnotator.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "annotator-client", "1.0.0"), string.Empty);
        Assert.True(requestAfterHostRestart.Accepted);
        _ = await thirdJoinRequested.Task.WaitAsync(TestTimeout);

        await resumedHost.InvokeAsync("EndSession", created.SessionId);
        Assert.Contains(
            "ended",
            await hostSessionEnded.Task.WaitAsync(TestTimeout),
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
        var oversizedRequest = new DirectJoinRequest(
            "unknown-session",
            "large-client",
            new string('x', 40_000));

        var exception = await Record.ExceptionAsync(
            () => connection.InvokeAsync(
                    "RequestToJoinHost",
                    oversizedRequest,
                    string.Empty)
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
        await using var host = CreateConnection(factory, "host-rate", "Host");
        await using var annotator = CreateConnection(factory, "annotator-rate", "Annotator");
        var joinRequested = CompletionSource<AnnotatorDescriptor>();
        var annotatorCredential = CompletionSource<SessionCredential>();
        var allPointersReceived = CompletionSource<int>();
        var receivedCount = 0;
        host.On<AnnotatorDescriptor>("AnnotatorJoinRequested", joinRequested.SetResult);
        host.On<PointerEventMessage>(
            "PointerReceived",
            _ =>
            {
                var count = Interlocked.Increment(ref receivedCount);
                if (count == 30)
                {
                    allPointersReceived.TrySetResult(count);
                }
            });
        annotator.On<SessionCredential>("SessionCredentialIssued", annotatorCredential.SetResult);
        await host.StartAsync();
        await annotator.StartAsync();
        var created = await host.InvokeAsync<CreateSessionResponse>(
            "CreateHostSession",
            CreateDisplay(),
            new ClientProfile(),
            2,
            string.Empty);
        _ = await annotator.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "annotator-rate", "1.0.0"),
            string.Empty);
        var pending = await joinRequested.Task.WaitAsync(TestTimeout);
        await host.InvokeAsync("ApproveAnnotator", created.SessionId, pending.ConnectionId);
        _ = await annotatorCredential.Task.WaitAsync(TestTimeout);

        for (var sequence = 0; sequence < 30; sequence++)
        {
            await annotator.InvokeAsync(
                "SendPointer",
                CreatePointer(created.SessionId, sequence));
        }

        await Assert.ThrowsAsync<HubException>(
            () => annotator.InvokeAsync(
                "SendPointer",
                CreatePointer(created.SessionId, 30)));
        Assert.Equal(30, await allPointersReceived.Task.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task ServerPassword_ScopesTheDirectoryToClientsThatShareIt()
    {
        using var factory = CreateFactory(requireServerPassword: true);
        await using var host = CreateConnection(factory, "host-group", "Host");
        await using var insider = CreateConnection(factory, "insider-group", "Insider");
        await using var outsider = CreateConnection(factory, "outsider-group", "Outsider");
        await host.StartAsync();
        await insider.StartAsync();
        await outsider.StartAsync();

        Assert.True(
            (await host.InvokeAsync<RelayCapabilities>("GetRelayCapabilities"))
                .ServerPasswordRequired);
        await Assert.ThrowsAsync<HubException>(
            () => host.InvokeAsync<CreateSessionResponse>(
                "CreateHostSession",
                CreateDisplay(),
                new ClientProfile(),
                2,
                string.Empty));

        await host.InvokeAsync("EnterRelayGroup", "shared-key");
        await insider.InvokeAsync("EnterRelayGroup", "shared-key");
        await outsider.InvokeAsync("EnterRelayGroup", "other-key");
        var created = await host.InvokeAsync<CreateSessionResponse>(
            "CreateHostSession",
            CreateDisplay(),
            new ClientProfile(),
            2,
            string.Empty);

        var insiderView = await insider.InvokeAsync<AvailableHostDescriptor[]>(
            "GetAvailableHosts");
        var outsiderView = await outsider.InvokeAsync<AvailableHostDescriptor[]>(
            "GetAvailableHosts");
        var outsiderJoin = await outsider.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "outsider-group", "1.0.0"), string.Empty);

        Assert.Equal(created.SessionId, Assert.Single(insiderView).SessionId);
        Assert.Empty(outsiderView);
        Assert.False(outsiderJoin.Accepted);
    }

    [Fact]
    public async Task ChangedServerPassword_HidesTheHostFromItsFormerPeerAndTellsItToRelist()
    {
        using var factory = CreateFactory(requireServerPassword: true);
        await using var host = CreateConnection(factory, "moving-host", "Host");
        await using var peer = CreateConnection(factory, "staying-peer", "Peer");
        var peerRelisted = CompletionSource<bool>();
        peer.On("HostDirectoryChanged", () => peerRelisted.TrySetResult(true));
        await host.StartAsync();
        await peer.StartAsync();
        await host.InvokeAsync("EnterRelayGroup", "first-password-key");
        await peer.InvokeAsync("EnterRelayGroup", "first-password-key");
        var created = await host.InvokeAsync<CreateSessionResponse>(
            "CreateHostSession",
            CreateDisplay(),
            new ClientProfile(),
            2,
            string.Empty);
        Assert.Single(await peer.InvokeAsync<AvailableHostDescriptor[]>("GetAvailableHosts"));

        await host.InvokeAsync("EnterRelayGroup", "second-password-key");

        Assert.True(await peerRelisted.Task.WaitAsync(TestTimeout));
        Assert.Empty(await peer.InvokeAsync<AvailableHostDescriptor[]>("GetAvailableHosts"));
        var staleJoin = await peer.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "staying-peer", "1.0.0"),
            string.Empty);
        Assert.False(staleJoin.Accepted);
    }

    [Fact]
    public async Task ChangedServerPassword_CancelsAJoinRequestItLeavesBehind()
    {
        using var factory = CreateFactory(requireServerPassword: true);
        await using var host = CreateConnection(factory, "moving-host", "Host");
        await using var peer = CreateConnection(factory, "requesting-peer", "Peer");
        var pending = CompletionSource<AnnotatorDescriptor>();
        var cancelled = CompletionSource<string>();
        var peerEnded = CompletionSource<string>();
        host.On<AnnotatorDescriptor>("AnnotatorJoinRequested", pending.SetResult);
        host.On<string>("AnnotatorJoinCancelled", cancelled.SetResult);
        peer.On<string>("SessionEnded", peerEnded.SetResult);
        await host.StartAsync();
        await peer.StartAsync();
        await host.InvokeAsync("EnterRelayGroup", "first-password-key");
        await peer.InvokeAsync("EnterRelayGroup", "first-password-key");
        var created = await host.InvokeAsync<CreateSessionResponse>(
            "CreateHostSession",
            CreateDisplay(),
            new ClientProfile(),
            2,
            string.Empty);
        Assert.True((await peer.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "requesting-peer", "1.0.0"),
            string.Empty)).Accepted);
        var requested = await pending.Task.WaitAsync(TestTimeout);

        await host.InvokeAsync("EnterRelayGroup", "second-password-key");

        Assert.Equal(requested.ConnectionId, await cancelled.Task.WaitAsync(TestTimeout));
        Assert.Contains(
            "server password",
            await peerEnded.Task.WaitAsync(TestTimeout),
            StringComparison.Ordinal);
        await Assert.ThrowsAsync<HubException>(
            () => host.InvokeAsync(
                "ApproveAnnotator",
                created.SessionId,
                requested.ConnectionId));
    }

    [Fact]
    public async Task ServerPassword_IsNotRequiredOnAnOpenRelay()
    {
        using var factory = CreateFactory();
        await using var host = CreateConnection(factory, "open-host", "Host");
        await using var annotator = CreateConnection(factory, "open-annotator", "Annotator");
        await host.StartAsync();
        await annotator.StartAsync();

        Assert.False(
            (await host.InvokeAsync<RelayCapabilities>("GetRelayCapabilities"))
                .ServerPasswordRequired);
        var created = await host.InvokeAsync<CreateSessionResponse>(
            "CreateHostSession",
            CreateDisplay(),
            new ClientProfile(),
            2,
            string.Empty);
        var available = await annotator.InvokeAsync<AvailableHostDescriptor[]>(
            "GetAvailableHosts");

        Assert.Equal(created.SessionId, Assert.Single(available).SessionId);
    }

    [Fact]
    public async Task AnnotationColors_AreNudgedApartAndHandedBackWhenTheHolderLeaves()
    {
        using var factory = CreateFactory();
        await using var host = CreateConnection(factory, "host-colour", "Host");
        await using var firstAnnotator = CreateConnection(factory, "annotator-one", "Annotator One");
        await using var secondAnnotator = CreateConnection(factory, "annotator-two", "Annotator Two");
        var firstPending = CompletionSource<AnnotatorDescriptor>();
        var secondPending = CompletionSource<AnnotatorDescriptor>();
        var requestCount = 0;
        host.On<AnnotatorDescriptor>(
            "AnnotatorJoinRequested",
            annotator =>
            {
                if (Interlocked.Increment(ref requestCount) == 1)
                {
                    firstPending.TrySetResult(annotator);
                }
                else
                {
                    secondPending.TrySetResult(annotator);
                }
            });

        // Every colour the relay hands each annotator, newest last.
        var firstColors = new List<string>();
        var secondColors = new List<string>();
        var secondWasMoved = CompletionSource<string>();
        var secondGotItsPreferenceBack = CompletionSource<string>();
        firstAnnotator.On<string>("AnnotationColorAssigned", firstColors.Add);
        secondAnnotator.On<string>(
            "AnnotationColorAssigned",
            color =>
            {
                secondColors.Add(color);
                if (string.Equals(color, "#B388FF", StringComparison.Ordinal))
                {
                    secondGotItsPreferenceBack.TrySetResult(color);
                }
                else
                {
                    secondWasMoved.TrySetResult(color);
                }
            });

        await host.StartAsync();
        await firstAnnotator.StartAsync();
        await secondAnnotator.StartAsync();
        var created = await host.InvokeAsync<CreateSessionResponse>(
            "CreateHostSession",
            CreateDisplay(),
            new ClientProfile(),
            2,
            string.Empty);

        Assert.True((await firstAnnotator.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "annotator-one", "1.0.0"),
            string.Empty)).Accepted);
        var first = await firstPending.Task.WaitAsync(TestTimeout);
        await host.InvokeAsync("ApproveAnnotator", created.SessionId, first.ConnectionId);
        await firstAnnotator.InvokeAsync("SetAnnotationColorPreference", "#B388FF");

        Assert.True((await secondAnnotator.InvokeAsync<JoinResponse>(
            "RequestToJoinHost",
            new DirectJoinRequest(created.SessionId, "annotator-two", "1.0.0"),
            string.Empty)).Accepted);
        var second = await secondPending.Task.WaitAsync(TestTimeout);
        await host.InvokeAsync("ApproveAnnotator", created.SessionId, second.ConnectionId);

        // Both want violet; the one that got there first keeps it.
        await secondAnnotator.InvokeAsync("SetAnnotationColorPreference", "#B388FF");
        var moved = await secondWasMoved.Task.WaitAsync(TestTimeout);
        Assert.NotEqual("#B388FF", moved);
        Assert.Contains(moved, AnnotationColors.Palette);
        Assert.Equal("#B388FF", firstColors[^1]);

        await firstAnnotator.InvokeAsync("EndSession", created.SessionId);

        Assert.Equal(
            "#B388FF",
            await secondGotItsPreferenceBack.Task.WaitAsync(TestTimeout));
    }

    private static WebApplicationFactory<Program> CreateFactory(
        bool requireServerPassword = false) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(
                builder =>
                {
                    builder.UseEnvironment("Development");
                    builder.UseSetting(
                        "Sessions:RequireServerPassword",
                        requireServerPassword.ToString());
                });

    private static HubConnection CreateConnection(
        WebApplicationFactory<Program> factory,
        string clientInstanceId,
        string displayName,
        string? applicationInstanceId = null)
    {
        var server = factory.Server;
        applicationInstanceId ??= clientInstanceId;
        var query = $"?clientInstanceId={Uri.EscapeDataString(clientInstanceId)}"
            + $"&applicationInstanceId={Uri.EscapeDataString(applicationInstanceId)}"
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
