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
        var join = await presenter.RequestToJoinSessionAsync(created.PairingCode);
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
}
