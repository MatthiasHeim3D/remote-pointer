using RemotePointer.Client.Native;
using RemotePointer.Client.Services;
using RemotePointer.Client.Tests.Fakes;
using RemotePointer.Client.ViewModels;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task RestoreSessions_SkipsBothRolesForSharedProfileTestClients()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var receiverRelay = new FakeRelayClient
        {
            Credential = new SessionCredential(
                "receiver-session",
                ClientRole.Receiver,
                "shared-client",
                new string('s', 32),
                new string('r', 32),
                expiresAt),
            SessionId = "receiver-session",
            ResumeResult = true,
        };
        var presenterRelay = new FakeRelayClient
        {
            Credential = new SessionCredential(
                "presenter-session",
                ClientRole.Presenter,
                "shared-client",
                new string('t', 32),
                new string('u', 32),
                expiresAt),
            SessionId = "presenter-session",
            ResumeResult = true,
        };
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: receiverRelay,
            presenterRelayClient: presenterRelay);

        await viewModel.RestoreSessionsAsync();

        Assert.Equal(0, receiverRelay.ResumeCount);
        Assert.Equal(0, presenterRelay.ResumeCount);
        Assert.Contains(
            "skipped",
            viewModel.ReceiverConnectionMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReceiverSession_ApprovesPresenterAndDisplaysFreshPointerWithAcknowledgement()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);

        viewModel.CreateReceiverSessionCommand.Execute(null);
        var presenter = new PresenterDescriptor(
            "connection-1",
            "presenter-1",
            "Presenter PC",
            "1.0.0");
        relay.RaiseJoinRequest(presenter);
        viewModel.ApprovePresenterCommand.Execute(null);
        var pointer = new PointerEventMessage(
            Guid.NewGuid(),
            "session-1",
            1,
            0.25d,
            0.75d,
            PointerKind.Click,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            2_000);

        relay.RaisePointer(pointer);

        Assert.Equal("AB2D4E", viewModel.PairingCode);
        Assert.Equal("connection-1", relay.ApprovedPresenter?.ConnectionId);
        Assert.Equal(new NormalizedPoint(0.25d, 0.75d), Assert.Single(overlay.Markers));
        Assert.Equal(pointer.EventId, relay.SentAcknowledgement?.EventId);
        Assert.False(viewModel.CanSelectMonitor);
    }

    [Fact]
    public void ReceiverSession_DropsExpiredPointerWithoutAcknowledging()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);
        viewModel.CreateReceiverSessionCommand.Execute(null);
        var expired = new PointerEventMessage(
            Guid.NewGuid(),
            "session-1",
            1,
            0.5d,
            0.5d,
            PointerKind.Click,
            DateTimeOffset.UtcNow.AddSeconds(-3).ToUnixTimeMilliseconds(),
            2_000);

        relay.RaisePointer(expired);

        Assert.Empty(overlay.Markers);
        Assert.Null(relay.SentAcknowledgement);
    }

    [Fact]
    public void ReceiverWithoutActiveSession_DropsPointerWithoutAcknowledging()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);
        overlay.Show(monitor);
        var pointer = new PointerEventMessage(
            Guid.NewGuid(),
            "session-1",
            1,
            0.5d,
            0.5d,
            PointerKind.Click,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            2_000);

        relay.RaisePointer(pointer);

        Assert.Empty(overlay.Markers);
        Assert.Null(relay.SentAcknowledgement);
    }

    [Fact]
    public void ReceiverSession_EndFailureKeepsLocalSessionActive()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        relay.EndException = new InvalidOperationException("Relay unavailable.");
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);
        viewModel.CreateReceiverSessionCommand.Execute(null);
        overlay.Show(monitor);

        viewModel.EndReceiverSessionCommand.Execute(null);

        Assert.True(viewModel.HasReceiverSession);
        Assert.True(overlay.IsVisible);
        Assert.True(viewModel.IsError);
        Assert.Contains("could not confirm", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_LoadsAndSelectsFirstMonitor()
    {
        var primary = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([primary]),
            overlay);

        Assert.Single(viewModel.Monitors);
        Assert.Same(primary, viewModel.SelectedMonitor);
        Assert.False(viewModel.IsError);
    }

    [Fact]
    public void RefreshMonitors_PreservesSelectionByDisplayId()
    {
        var first = CreateMonitor("DISPLAY1", isPrimary: true);
        var second = CreateMonitor("DISPLAY2", isPrimary: false);
        var monitorService = new FakeMonitorService([first, second]);
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(monitorService, overlay)
        {
            SelectedMonitor = second,
        };

        var refreshedSecond = CreateMonitor("DISPLAY2", isPrimary: false, width: 2_560);
        monitorService.Monitors = [first, refreshedSecond];
        viewModel.RefreshMonitors();

        Assert.Same(refreshedSecond, viewModel.SelectedMonitor);
    }

    [Fact]
    public void RefreshMonitors_RemovesOverlayWhenSelectionDisconnects()
    {
        var first = CreateMonitor("DISPLAY1", isPrimary: true);
        var second = CreateMonitor("DISPLAY2", isPrimary: false);
        var monitorService = new FakeMonitorService([first, second]);
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(monitorService, overlay)
        {
            SelectedMonitor = second,
        };
        overlay.Show(second);

        monitorService.Monitors = [first];
        viewModel.RefreshMonitors();

        Assert.True(overlay.HideWasCalled);
        Assert.False(viewModel.IsOverlayVisible);
        Assert.True(viewModel.IsError);
        Assert.Contains("disconnected", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Same(first, viewModel.SelectedMonitor);
    }

    [Fact]
    public void ShowOverlayCommand_ShowsOverlayOnSelection()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay);

        viewModel.ShowOverlayCommand.Execute(null);

        Assert.Same(monitor, overlay.ShownMonitor);
        Assert.True(viewModel.IsOverlayVisible);
    }

    [Theory]
    [InlineData("TopLeft", 0d, 0d)]
    [InlineData("TopRight", 1d, 0d)]
    [InlineData("Center", 0.5d, 0.5d)]
    [InlineData("BottomLeft", 0d, 1d)]
    [InlineData("BottomRight", 1d, 1d)]
    public void ShowPresetMarkerCommand_SendsExpectedPoint(
        string preset,
        double expectedX,
        double expectedY)
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay);
        overlay.Show(monitor);

        viewModel.ShowPresetMarkerCommand.Execute(preset);

        Assert.Equal(new NormalizedPoint(expectedX, expectedY), Assert.Single(overlay.Markers));
    }

    [Fact]
    public void ShowCustomMarkerCommand_RejectsOutOfRangeCoordinate()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay)
        {
            CustomNormalizedX = -0.1d,
            CustomNormalizedY = 0.5d,
        };
        overlay.Show(monitor);

        viewModel.ShowCustomMarkerCommand.Execute(null);

        Assert.Empty(overlay.Markers);
        Assert.True(viewModel.IsError);
    }

    [Fact]
    public void OverlayDisconnectionState_IsPresentedAsError()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay);

        overlay.RaiseDisconnected();

        Assert.False(viewModel.IsOverlayVisible);
        Assert.True(viewModel.IsError);
        Assert.Contains("disconnected", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static MonitorDescriptor CreateMonitor(
        string id,
        bool isPrimary,
        int width = 1_920) => new(
            Handle: 1,
            new DisplayDescriptor(id, id, width, 1_080, 1d, 0),
            new PhysicalRectangle(isPrimary ? 0 : -width, 0, width, 1_080),
            new PhysicalRectangle(isPrimary ? 0 : -width, 0, width, 1_040),
            isPrimary);

    private static FakeRelayClient CreateReceiverRelay()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(8);
        var credential = new SessionCredential(
            "session-1",
            ClientRole.Receiver,
            "receiver-1",
            new string('s', 32),
            new string('r', 32),
            expiresAt);
        return new FakeRelayClient
        {
            CreateResponse = new CreateSessionResponse(
                "session-1",
                "AB2D4E",
                new string('x', 32),
                credential,
                DateTimeOffset.UtcNow.AddMinutes(10)),
        };
    }

    private sealed class FakeMonitorService(IReadOnlyList<MonitorDescriptor> monitors)
        : IMonitorService
    {
        public IReadOnlyList<MonitorDescriptor> Monitors { get; set; } = monitors;

        public IReadOnlyList<MonitorDescriptor> GetMonitors() => Monitors;

        public MonitorDescriptor? FindByDisplayId(string displayId) =>
            Monitors.FirstOrDefault(
                monitor => string.Equals(
                    monitor.Display.DisplayId,
                    displayId,
                    StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeOverlayService : IReceiverOverlayService
    {
        public event EventHandler<OverlayStateChangedEventArgs>? StateChanged;

        public bool IsVisible { get; private set; }

        public bool HideWasCalled { get; private set; }

        public MonitorDescriptor? ShownMonitor { get; private set; }

        public List<NormalizedPoint> Markers { get; } = [];

        public void Show(MonitorDescriptor monitor)
        {
            ShownMonitor = monitor;
            IsVisible = true;
            StateChanged?.Invoke(
                this,
                new OverlayStateChangedEventArgs("Overlay active.", false, true));
        }

        public void Hide()
        {
            HideWasCalled = true;
            IsVisible = false;
            StateChanged?.Invoke(
                this,
                new OverlayStateChangedEventArgs("Overlay hidden.", false, false));
        }

        public bool ShowMarker(NormalizedPoint point)
        {
            if (!IsVisible)
            {
                return false;
            }

            Markers.Add(point);
            return true;
        }

        public void RaiseDisconnected()
        {
            IsVisible = false;
            StateChanged?.Invoke(
                this,
                new OverlayStateChangedEventArgs(
                    "The selected monitor was disconnected.",
                    true,
                    false));
        }

        public void Dispose()
        {
        }
    }
}
