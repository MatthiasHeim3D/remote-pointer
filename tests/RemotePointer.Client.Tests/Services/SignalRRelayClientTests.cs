using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using RemotePointer.Client.Configuration;
using RemotePointer.Client.Services;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Tests.Services;

public sealed class SignalRRelayClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

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
            },
        };
        await using var receiver = new SignalRRelayClient(
            settings,
            new FixedClientInstanceIdProvider("receiver-client"),
            server.CreateHandler,
            HttpTransportType.LongPolling);
        await using var presenter = new SignalRRelayClient(
            settings,
            new FixedClientInstanceIdProvider("presenter-client"),
            server.CreateHandler,
            HttpTransportType.LongPolling);
        var joinRequested = CompletionSource<PresenterDescriptor>();
        var presenterApproved = CompletionSource<SessionStateMessage>();
        var pointerReceived = CompletionSource<PointerEventMessage>();
        var acknowledgementReceived = CompletionSource<PointerAcknowledgement>();
        var receiverEnded = CompletionSource<string>();
        var presenterEnded = CompletionSource<string>();
        receiver.PresenterJoinRequested += (_, e) => joinRequested.TrySetResult(e.Presenter);
        presenter.SessionApproved += (_, e) => presenterApproved.TrySetResult(e.State);
        receiver.PointerReceived += (_, e) => pointerReceived.TrySetResult(e.PointerEvent);
        presenter.PointerDisplayed += (_, e) => acknowledgementReceived.TrySetResult(e.Acknowledgement);
        receiver.SessionEnded += (_, e) => receiverEnded.TrySetResult(e.Reason);
        presenter.SessionEnded += (_, e) => presenterEnded.TrySetResult(e.Reason);

        var created = await receiver.CreateReceiverSessionAsync(CreateDisplay());
        var join = await presenter.RequestToJoinReceiverAsync(created.SessionId);
        var descriptor = await joinRequested.Task.WaitAsync(TestTimeout);
        await receiver.ApprovePresenterAsync(created.SessionId, descriptor.ConnectionId);
        var approved = await presenterApproved.Task.WaitAsync(TestTimeout);
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
        Assert.Equal(ClientRole.Presenter, presenter.Credential?.Role);
        Assert.True(await presenter.SendPointerAsync(pointer));
        Assert.Equal(pointer, await pointerReceived.Task.WaitAsync(TestTimeout));

        var acknowledgement = new PointerAcknowledgement(
            pointer.EventId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.True(await receiver.AcknowledgePointerAsync(acknowledgement));
        Assert.Equal(
            acknowledgement,
            await acknowledgementReceived.Task.WaitAsync(TestTimeout));

        await receiver.EndSessionAsync();
        Assert.Contains(
            "ended",
            await receiverEnded.Task.WaitAsync(TestTimeout),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ended",
            await presenterEnded.Task.WaitAsync(TestTimeout),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProtectedReceiverCredential_ResumesAfterClientRestartAndRotatesToken()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        var server = factory.Server;
        var settings = CreateSettings(server.BaseAddress);
        using var sessionDirectory = new TemporaryDirectory();
        var store = new ProtectedSessionStore(
            new ReversingDataProtector(),
            sessionDirectory: sessionDirectory.Path);
        await using var receiver = new SignalRRelayClient(
            settings,
            new FixedClientInstanceIdProvider("receiver-client"),
            server.CreateHandler,
            HttpTransportType.LongPolling,
            ClientRole.Receiver,
            store);
        await using var presenter = new SignalRRelayClient(
            settings,
            new FixedClientInstanceIdProvider("presenter-client"),
            server.CreateHandler,
            HttpTransportType.LongPolling);
        var joinRequested = CompletionSource<PresenterDescriptor>();
        var presenterApproved = CompletionSource<SessionStateMessage>();
        var receiverApproved = CompletionSource<SessionStateMessage>();
        receiver.PresenterJoinRequested += (_, e) => joinRequested.TrySetResult(e.Presenter);
        receiver.SessionApproved += (_, e) => receiverApproved.TrySetResult(e.State);
        presenter.SessionApproved += (_, e) => presenterApproved.TrySetResult(e.State);
        var created = await receiver.CreateReceiverSessionAsync(CreateDisplay());
        Assert.Equal(created.Credential, store.Load(ClientRole.Receiver, "receiver-client"));
        _ = await presenter.RequestToJoinReceiverAsync(created.SessionId);
        var descriptor = await joinRequested.Task.WaitAsync(TestTimeout);
        await receiver.ApprovePresenterAsync(created.SessionId, descriptor.ConnectionId);
        _ = await presenterApproved.Task.WaitAsync(TestTimeout);
        _ = await receiverApproved.Task.WaitAsync(TestTimeout);
        var originalReconnectToken = Assert.IsType<SessionCredential>(receiver.Credential)
            .ReconnectToken;
        Assert.Equal(
            originalReconnectToken,
            store.Load(ClientRole.Receiver, "receiver-client")?.ReconnectToken);

        await receiver.DisposeAsync();

        await using var recoveredReceiver = new SignalRRelayClient(
            settings,
            new FixedClientInstanceIdProvider("receiver-client"),
            server.CreateHandler,
            HttpTransportType.LongPolling,
            ClientRole.Receiver,
            store);
        var recoveredState = CompletionSource<SessionStateMessage>();
        var pointerReceived = CompletionSource<PointerEventMessage>();
        recoveredReceiver.SessionApproved += (_, e) => recoveredState.TrySetResult(e.State);
        recoveredReceiver.PointerReceived += (_, e) => pointerReceived.TrySetResult(e.PointerEvent);

        Assert.True(await recoveredReceiver.TryResumeSessionAsync());
        Assert.True((await recoveredState.Task.WaitAsync(TestTimeout)).Approved);
        Assert.NotEqual(originalReconnectToken, recoveredReceiver.Credential?.ReconnectToken);
        Assert.Equal(
            recoveredReceiver.Credential,
            store.Load(ClientRole.Receiver, "receiver-client"));

        var pointer = new PointerEventMessage(
            Guid.NewGuid(),
            created.SessionId,
            0,
            0.5d,
            0.5d,
            PointerKind.Click,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            2_000);
        Assert.True(await presenter.SendPointerAsync(pointer));
        Assert.Equal(pointer, await pointerReceived.Task.WaitAsync(TestTimeout));
        await recoveredReceiver.EndSessionAsync();
    }

    private static ClientSettings CreateSettings(Uri baseAddress) => new()
    {
        Server = new ServerSettings
        {
            BaseUrl = baseAddress.ToString(),
            ReconnectDelaysSeconds = [0, 1],
        },
    };

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
                $"RemotePointer.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
