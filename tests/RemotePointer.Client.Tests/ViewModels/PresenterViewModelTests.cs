using RemotePointer.Client.Services;
using RemotePointer.Client.Tests.Fakes;
using RemotePointer.Client.ViewModels;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Tests.ViewModels;

public sealed class PresenterViewModelTests
{
    [Fact]
    public async Task DiscoveryInitialization_LoadsReceiversAndDirectJoinRequestsSelection()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient
        {
            Capabilities = new RelayCapabilities(true),
            AvailableReceivers =
            [
                new AvailableReceiverDescriptor(
                    "session-visible",
                    "Receiver PC",
                    ProfilePicturePng: [1, 2, 3]),
            ],
        };
        using var viewModel = new PresenterViewModel(service, relay);

        await viewModel.InitializeAsync();
        viewModel.JoinDiscoveredReceiverCommand.Execute(null);

        Assert.True(viewModel.ReceiverDiscoveryEnabled);
        Assert.Equal("session-visible", relay.RequestedReceiverSessionId);
        Assert.True(viewModel.IsJoinPending);
        Assert.Equal("Receiver PC", viewModel.CurrentReceiverName);
        Assert.Equal(new byte[] { 1, 2, 3 }, viewModel.CurrentReceiverProfilePicturePng);
        Assert.Equal(
            "Request sent. Waiting for approval.",
            viewModel.SenderConnectionStatusLabel);
    }

    [Fact]
    public async Task DiscoveryInitialization_RemainsDisabledWhenServerDisallowsIt()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient
        {
            Capabilities = new RelayCapabilities(false),
            AvailableReceivers =
            [
                new AvailableReceiverDescriptor("session-hidden", "Hidden Receiver"),
            ],
        };
        using var viewModel = new PresenterViewModel(service, relay);

        await viewModel.InitializeAsync();

        Assert.False(viewModel.ReceiverDiscoveryEnabled);
        Assert.Empty(viewModel.AvailableReceivers);
        Assert.False(viewModel.JoinDiscoveredReceiverCommand.CanExecute(null));
    }

    [Fact]
    public async Task DirectoryChange_AutomaticallyRefreshesAvailableReceivers()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient
        {
            Capabilities = new RelayCapabilities(true),
        };
        using var viewModel = new PresenterViewModel(service, relay);
        await viewModel.InitializeAsync();
        relay.AvailableReceivers =
        [
            new AvailableReceiverDescriptor("new-session", "New receiver"),
        ];

        relay.RaiseReceiverDirectoryChanged();

        Assert.Equal("new-session", Assert.Single(viewModel.AvailableReceivers).SessionId);
    }

    [Fact]
    public void ApprovedPointer_IsSentAndAcknowledgementLatencyIsShown()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient();
        using var viewModel = new PresenterViewModel(service, relay);
        relay.RaiseApproved(
            new SessionStateMessage(
                "session-1",
                true,
                new DisplayDescriptor("display", "Display", 2_560, 1_440, 1d, 0),
                DateTimeOffset.UtcNow.AddHours(8)));

        service.RaisePointer(new NormalizedPoint(0.25d, 0.75d));
        var sent = Assert.IsType<PointerEventMessage>(relay.SentPointer);
        relay.RaiseAcknowledgement(
            new PointerAcknowledgement(sent.EventId, sent.SentAtUnixMilliseconds + 42));

        Assert.Equal("session-1", sent.SessionId);
        Assert.Equal(0.25d, sent.NormalizedX);
        Assert.Equal(0.75d, sent.NormalizedY);
        Assert.Equal(2_000, sent.TimeToLiveMilliseconds);
        Assert.True(sent.SequenceNumber > 1_000_000);
        Assert.Contains("2560 × 1440", viewModel.ReceiverDisplayShape, StringComparison.Ordinal);
        Assert.Contains("42 ms", viewModel.LastAcknowledgement, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconnectingPointer_IsDroppedInsteadOfQueued()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient();
        using var viewModel = new PresenterViewModel(service, relay);
        relay.RaiseApproved(
            new SessionStateMessage(
                "session-1",
                true,
                new DisplayDescriptor("display", "Display", 1_920, 1_080, 1d, 0),
                DateTimeOffset.UtcNow.AddHours(8)));
        relay.RaiseConnectionStatus(RelayConnectionStatus.Reconnecting, "Reconnecting.");

        service.RaisePointer(new NormalizedPoint(0.5d, 0.5d));

        Assert.True(viewModel.IsError);
        Assert.Contains("dropped", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GesturePointer_PreservesKindGestureIdAndPathBatch()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient();
        using var viewModel = new PresenterViewModel(service, relay);
        relay.RaiseApproved(
            new SessionStateMessage(
                "session-1",
                true,
                new DisplayDescriptor("display", "Display", 1_920, 1_080, 1d, 0),
                DateTimeOffset.UtcNow.AddHours(8)));
        var gestureId = Guid.NewGuid();
        NormalizedPoint[] pathPoints = [new(0.2d, 0.3d), new(0.3d, 0.4d)];

        service.RaisePointer(
            new NormalizedPoint(0.3d, 0.4d),
            PointerKind.PathUpdate,
            gestureId,
            pathPoints: pathPoints);

        var sent = Assert.IsType<PointerEventMessage>(relay.SentPointer);
        Assert.Equal(PointerKind.PathUpdate, sent.Kind);
        Assert.Equal(gestureId, sent.GestureId);
        Assert.Equal(pathPoints, sent.PathPoints);
    }

    [Fact]
    public void SessionEnded_ExitsPointingAndClearsApproval()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient();
        using var viewModel = new PresenterViewModel(service, relay);
        relay.RaiseApproved(
            new SessionStateMessage(
                "session-1",
                true,
                new DisplayDescriptor("display", "Display", 1_920, 1_080, 1d, 0),
                DateTimeOffset.UtcNow.AddHours(8)));

        relay.RaiseSessionEnded("Ended by receiver.");

        Assert.False(viewModel.IsSessionApproved);
        Assert.Equal(1, service.ExitCount);
    }

    [Fact]
    public void EndSessionFailure_KeepsPresenterSessionActive()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient
        {
            EndException = new InvalidOperationException("Relay unavailable."),
        };
        using var viewModel = new PresenterViewModel(service, relay);
        relay.RaiseApproved(
            new SessionStateMessage(
                "session-1",
                true,
                new DisplayDescriptor("display", "Display", 1_920, 1_080, 1d, 0),
                DateTimeOffset.UtcNow.AddHours(8)));

        viewModel.EndSessionCommand.Execute(null);

        Assert.True(viewModel.IsSessionApproved);
        Assert.True(viewModel.IsError);
        Assert.Contains("could not confirm", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalibrateCommand_UsesSyncedReceiverAspectRatio()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient();
        using var viewModel = new PresenterViewModel(service, relay);
        relay.RaiseApproved(
            new SessionStateMessage(
                "session-1",
                true,
                new DisplayDescriptor("display", "Display", 2_560, 1_440, 1d, 0),
                DateTimeOffset.UtcNow.AddHours(8),
                ReceiverClientInstanceId: "receiver-client-id"));

        viewModel.CalibrateCommand.Execute(null);

        Assert.Equal(16d / 9d, service.RequestedAspectRatio, precision: 12);
        Assert.Equal("receiver-client-id", service.CalibrationIdentity);
    }

    [Fact]
    public void ReceiverDisplayChange_UpdatesShapeAndInvalidatesDifferentAspectCalibration()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient();
        using var viewModel = new PresenterViewModel(service, relay);
        relay.RaiseApproved(
            new SessionStateMessage(
                "session-1",
                true,
                new DisplayDescriptor("display", "Display", 1_920, 1_080, 1d, 0),
                DateTimeOffset.UtcNow.AddHours(8)));

        relay.RaiseReceiverDisplayChanged(
            new DisplayDescriptor("display", "Display", 1_200, 1_920, 1d, 90));

        Assert.Contains("1200 × 1920", viewModel.ReceiverDisplayShape, StringComparison.Ordinal);
        Assert.Equal(1_200d / 1_920d, service.UpdatedAspectRatio, precision: 12);
    }

    [Fact]
    public void LocalDisplayChange_InvalidatesCalibration()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new PresenterViewModel(service);

        viewModel.HandleLocalDisplayConfigurationChanged();

        Assert.Equal(1, service.InvalidateCount);
    }

    [Fact]
    public void TogglePointingMode_OpensCalibrationWhenInactive()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new PresenterViewModel(service);

        viewModel.TogglePointingMode();

        Assert.Equal(1, service.ToggleCount);
        Assert.False(viewModel.IsError);
    }

    [Fact]
    public void TogglePointingMode_ForwardsWhenReady()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new PresenterViewModel(service);
        service.RaiseState(TargetRegionState.Ready, "Ready");

        viewModel.TogglePointingMode();

        Assert.Equal(1, service.ToggleCount);
    }

    [Fact]
    public void PointerCaptured_UpdatesCountAndCoordinates()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new PresenterViewModel(service);

        service.RaisePointer(new NormalizedPoint(0.25d, 0.75d));

        Assert.Equal(1, viewModel.CapturedPointerCount);
        Assert.Contains("0.2500", viewModel.LastPointer, StringComparison.Ordinal);
        Assert.Contains("0.7500", viewModel.LastPointer, StringComparison.Ordinal);
    }

    [Fact]
    public void StateChanges_UpdatePointingAndStatusProperties()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new PresenterViewModel(service);

        service.RaiseState(TargetRegionState.Pointing, "Pointing active");

        Assert.True(viewModel.IsPointing);
        Assert.Equal("Stop pointing", viewModel.PointingActionLabel);
        Assert.Equal("Pointing", viewModel.StateLabel);
        Assert.Equal("Pointing active", viewModel.StatusMessage);
        Assert.False(viewModel.IsError);
    }

    [Fact]
    public void ReportHotKeyRegistrationFailure_PresentsError()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new PresenterViewModel(service);

        viewModel.ReportHotKeyRegistrationFailure("Hotkey unavailable.");

        Assert.True(viewModel.IsError);
        Assert.Equal("Hotkey unavailable.", viewModel.StatusMessage);
    }

    private sealed class FakeTargetRegionService : ITargetRegionService
    {
        public event EventHandler<TargetRegionStateChangedEventArgs>? StateChanged;

        public event EventHandler<PointerCapturedEventArgs>? PointerCaptured;

        public TargetRegionState State { get; private set; }

        public int BeginCalibrationCount { get; private set; }

        public double RequestedAspectRatio { get; private set; }

        public double UpdatedAspectRatio { get; private set; }

        public int InvalidateCount { get; private set; }

        public int ToggleCount { get; private set; }

        public int ExitCount { get; private set; }

        public string? CalibrationIdentity { get; private set; }

        public bool ShowExitHint { get; private set; } = true;

        public void SetCalibrationIdentity(string? receiverIdentity) =>
            CalibrationIdentity = receiverIdentity;

        public void SetShowExitHint(bool showExitHint) => ShowExitHint = showExitHint;

        public void BeginCalibration(double expectedAspectRatio)
        {
            BeginCalibrationCount++;
            RequestedAspectRatio = expectedAspectRatio;
        }

        public void UpdateExpectedAspectRatio(double expectedAspectRatio) =>
            UpdatedAspectRatio = expectedAspectRatio;

        public void InvalidateCalibration(string message)
        {
            _ = message;
            InvalidateCount++;
        }

        public void TogglePointingMode() => ToggleCount++;

        public void ExitPointingMode()
        {
            ExitCount++;
        }

        public void RaiseState(TargetRegionState state, string message, bool isError = false)
        {
            State = state;
            StateChanged?.Invoke(
                this,
                new TargetRegionStateChangedEventArgs(state, message, isError));
        }

        public void RaisePointer(
            NormalizedPoint point,
            PointerKind kind = PointerKind.Click,
            Guid? gestureId = null,
            string? text = null,
            NormalizedPoint[]? pathPoints = null) =>
            PointerCaptured?.Invoke(
                this,
                new PointerCapturedEventArgs(point, kind, gestureId, text, pathPoints));

        public void Dispose()
        {
        }
    }
}
