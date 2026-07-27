using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using RemoteAnnotate.Client.Configuration;
using RemoteAnnotate.Client.Services;
using RemoteAnnotate.Contracts.Messages;

namespace RemoteAnnotate.Client.Tests.Services;

public sealed class SignalRRelayClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    // The relay requires a server password by default, so the clients under test present the
    // key derived from a shared one and exercise the group handshake on every connect.
    private const string SharedGroupKey = "shared-test-group-key";

    [Fact]
    public async Task TwoClients_CompleteApprovedPointerAndTerminationWorkflow()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        var server = factory.Server;
        var settings = new ClientSettings
        {
            Server = new ServerSettings
            {
                BaseUrl = server.BaseAddress.ToString(),
                ReconnectDelaysSeconds = [0, 1],
                PasswordKey = SharedGroupKey,
            },
        };
        await using var host = new SignalRRelayClient(
            settings,
            new FixedClientInstanceIdProvider("host-client"),
            server.CreateHandler,
            HttpTransportType.LongPolling);
        await using var annotator = new SignalRRelayClient(
            settings,
            new FixedClientInstanceIdProvider("annotator-client"),
            server.CreateHandler,
            HttpTransportType.LongPolling);
        var joinRequested = CompletionSource<AnnotatorDescriptor>();
        var annotatorApproved = CompletionSource<SessionStateMessage>();
        var pointerReceived = CompletionSource<PointerEventMessage>();
        var acknowledgementReceived = CompletionSource<PointerAcknowledgement>();
        var hostEnded = CompletionSource<string>();
        var annotatorEnded = CompletionSource<string>();
        host.AnnotatorJoinRequested += (_, e) => joinRequested.TrySetResult(e.Annotator);
        annotator.SessionApproved += (_, e) => annotatorApproved.TrySetResult(e.State);
        host.PointerReceived += (_, e) => pointerReceived.TrySetResult(e.PointerEvent);
        annotator.PointerDisplayed += (_, e) => acknowledgementReceived.TrySetResult(e.Acknowledgement);
        host.SessionEnded += (_, e) => hostEnded.TrySetResult(e.Reason);
        annotator.SessionEnded += (_, e) => annotatorEnded.TrySetResult(e.Reason);

        var created = await host.CreateHostSessionAsync(CreateDisplay());
        var join = await annotator.RequestToJoinHostAsync(created.SessionId);
        var descriptor = await joinRequested.Task.WaitAsync(TestTimeout);
        await host.ApproveAnnotatorAsync(created.SessionId, descriptor.ConnectionId);
        var approved = await annotatorApproved.Task.WaitAsync(TestTimeout);
        var pointer = new PointerEventMessage(
            Guid.NewGuid(),
            created.SessionId,
            0,
            0.25d,
            0.75d,
            PointerKind.Click,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            2_000);

        Assert.True(join.Accepted);
        Assert.True(approved.Approved);
        Assert.Equal(ClientRole.Annotator, annotator.Credential?.Role);
        Assert.True(await annotator.SendPointerAsync(pointer));
        Assert.Equal(
            pointer with { AnnotatorId = "annotator-client" },
            await pointerReceived.Task.WaitAsync(TestTimeout));

        var acknowledgement = new PointerAcknowledgement(
            pointer.EventId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.True(await host.AcknowledgePointerAsync(acknowledgement));
        Assert.Equal(
            acknowledgement,
            await acknowledgementReceived.Task.WaitAsync(TestTimeout));

        await host.DisconnectAllConnectionsAsync();
        Assert.Contains(
            "host",
            await annotatorEnded.Task.WaitAsync(TestTimeout),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            created.SessionId,
            Assert.Single(await annotator.GetAvailableHostsAsync()).SessionId);

        await host.EndSessionAsync();
        Assert.Contains(
            "ended",
            await hostEnded.Task.WaitAsync(TestTimeout),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisposingHost_EndsSessionAndRequiresNewAnnotatorRequest()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        var server = factory.Server;
        var settings = CreateSettings(server.BaseAddress);
        using var sessionDirectory = new TemporaryDirectory();
        var store = new ProtectedSessionStore(
            new ReversingDataProtector(),
            sessionDirectory: sessionDirectory.Path);
        await using var host = new SignalRRelayClient(
            settings,
            new FixedClientInstanceIdProvider("host-client"),
            server.CreateHandler,
            HttpTransportType.LongPolling,
            ClientRole.Host,
            store);
        await using var annotator = new SignalRRelayClient(
            settings,
            new FixedClientInstanceIdProvider("annotator-client"),
            server.CreateHandler,
            HttpTransportType.LongPolling);
        var joinRequested = CompletionSource<AnnotatorDescriptor>();
        var annotatorApproved = CompletionSource<SessionStateMessage>();
        var hostApproved = CompletionSource<SessionStateMessage>();
        host.AnnotatorJoinRequested += (_, e) => joinRequested.TrySetResult(e.Annotator);
        host.SessionApproved += (_, e) => hostApproved.TrySetResult(e.State);
        annotator.SessionApproved += (_, e) => annotatorApproved.TrySetResult(e.State);
        var annotatorEnded = CompletionSource<string>();
        annotator.SessionEnded += (_, e) => annotatorEnded.TrySetResult(e.Reason);
        var created = await host.CreateHostSessionAsync(CreateDisplay());
        Assert.Equal(created.Credential, store.Load(ClientRole.Host, "host-client"));
        _ = await annotator.RequestToJoinHostAsync(created.SessionId);
        var descriptor = await joinRequested.Task.WaitAsync(TestTimeout);
        await host.ApproveAnnotatorAsync(created.SessionId, descriptor.ConnectionId);
        _ = await annotatorApproved.Task.WaitAsync(TestTimeout);
        _ = await hostApproved.Task.WaitAsync(TestTimeout);
        var originalReconnectToken = Assert.IsType<SessionCredential>(host.Credential)
            .ReconnectToken;
        Assert.Equal(
            originalReconnectToken,
            store.Load(ClientRole.Host, "host-client")?.ReconnectToken);

        await host.DisposeAsync();

        Assert.Contains(
            "ended",
            await annotatorEnded.Task.WaitAsync(TestTimeout),
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(store.Load(ClientRole.Host, "host-client"));

        await using var recoveredHost = new SignalRRelayClient(
            settings,
            new FixedClientInstanceIdProvider("host-client"),
            server.CreateHandler,
            HttpTransportType.LongPolling,
            ClientRole.Host,
            store);
        Assert.Null(recoveredHost.Credential);
        Assert.False(await recoveredHost.TryResumeSessionAsync());

        var freshJoinRequested = CompletionSource<AnnotatorDescriptor>();
        recoveredHost.AnnotatorJoinRequested +=
            (_, e) => freshJoinRequested.TrySetResult(e.Annotator);
        var newSession = await recoveredHost.CreateHostSessionAsync(CreateDisplay());
        var freshJoin = await annotator.RequestToJoinHostAsync(newSession.SessionId);

        Assert.True(freshJoin.Accepted);
        _ = await freshJoinRequested.Task.WaitAsync(TestTimeout);
        await recoveredHost.EndSessionAsync();
    }

    [Fact]
    public async Task ChangedServerPassword_ReachesTheRelayBeforeTheNextOperation()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        var server = factory.Server;
        await using var host = new SignalRRelayClient(
            CreateSettings(server.BaseAddress),
            new FixedClientInstanceIdProvider("host-client"),
            server.CreateHandler,
            HttpTransportType.LongPolling);
        await using var peer = new SignalRRelayClient(
            CreateSettings(server.BaseAddress),
            new FixedClientInstanceIdProvider("peer-client"),
            server.CreateHandler,
            HttpTransportType.LongPolling);
        var peerRelisted = CompletionSource<bool>();
        peer.HostDirectoryChanged += (_, _) => peerRelisted.TrySetResult(true);
        var created = await host.CreateHostSessionAsync(CreateDisplay());
        Assert.Single(await peer.GetAvailableHostsAsync());

        // The host does nothing else afterwards: presenting the key has to be what carries
        // the change to the relay, not whatever hub call happens to come next.
        await host.SetServerPasswordKeyAsync("a-different-group-key");

        Assert.True(await peerRelisted.Task.WaitAsync(TestTimeout));
        Assert.Empty(await peer.GetAvailableHostsAsync());
        Assert.False((await peer.RequestToJoinHostAsync(created.SessionId)).Accepted);
    }

    private static ClientSettings CreateSettings(Uri baseAddress) => new()
    {
        Server = new ServerSettings
        {
            BaseUrl = baseAddress.ToString(),
            ReconnectDelaysSeconds = [0, 1],
            PasswordKey = SharedGroupKey,
        },
    };

    [Fact]
    public async Task Construction_SkipsAProfilePictureThatCannotBeDecoded()
    {
        using var directory = new TemporaryDirectory();
        var picturePath = System.IO.Path.Combine(directory.Path, "corrupt.png");
        File.WriteAllBytes(
            picturePath,
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02, 0x03]);
        var settings = new ClientSettings
        {
            Server = new ServerSettings
            {
                BaseUrl = "https://pointer.example.test",
                ReconnectDelaysSeconds = [0, 1],
            },
            Profile = new UserProfileSettings
            {
                UserName = "Ada Lovelace",
                PicturePath = picturePath,
            },
        };

        SignalRRelayClient? client = null;
        var exception = Record.Exception(
            () => client = new SignalRRelayClient(
                settings,
                new FixedClientInstanceIdProvider("client"),
                messageHandlerFactory: null));

        Assert.Null(exception);
        Assert.NotNull(client);
        await client.DisposeAsync();
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

    private sealed class FixedClientInstanceIdProvider(string value)
        : IClientInstanceIdProvider
    {
        public string GetClientInstanceId() => value;

        public string GetApplicationInstanceId() => $"{value}-application";
    }

    private sealed class ReversingDataProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => [.. plaintext.Reverse()];

        public byte[] Unprotect(byte[] protectedData) => [.. protectedData.Reverse()];
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RemoteAnnotate.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
