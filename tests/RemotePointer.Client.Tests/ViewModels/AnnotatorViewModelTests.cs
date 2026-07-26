using RemotePointer.Client.Configuration;
using RemotePointer.Client.Services;
using RemotePointer.Client.Tests.Fakes;
using RemotePointer.Client.ViewModels;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Tests.ViewModels;

public sealed class AnnotatorViewModelTests
{
    [Fact]
    public async Task DiscoveryInitialization_LoadsHostsAndDirectJoinRequestsSelection()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient
        {
            Capabilities = new RelayCapabilities(ServerPasswordRequired: false),
            AvailableHosts =
            [
                new AvailableHostDescriptor(
                    "session-visible",
                    "Host PC",
                    ProfilePicturePng: [1, 2, 3]),
            ],
        };
        using var viewModel = new AnnotatorViewModel(service, relay);

        await viewModel.InitializeAsync();
        viewModel.JoinDiscoveredHostCommand.Execute(null);

        Assert.Equal("session-visible", relay.RequestedHostSessionId);
        Assert.True(viewModel.IsJoinPending);
        Assert.Equal("Host PC", viewModel.CurrentHostName);
        Assert.Equal(new byte[] { 1, 2, 3 }, viewModel.CurrentHostProfilePicturePng);
        Assert.Equal(
            "Request sent. Waiting for approval.",
            viewModel.ConnectionStatusLabel);
        Assert.Equal("Cancel connection request", viewModel.EndSessionActionLabel);
        Assert.True(viewModel.EndSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task PendingRequest_CanBeCancelledFromTheAnnotatorPanel()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient
        {
            Capabilities = new RelayCapabilities(ServerPasswordRequired: false),
            AvailableHosts = [new AvailableHostDescriptor("session-visible", "Host PC")],
        };
        using var viewModel = new AnnotatorViewModel(service, relay);
        await viewModel.InitializeAsync();
        viewModel.JoinDiscoveredHostCommand.Execute(null);
        Assert.True(viewModel.IsJoinPending);

        viewModel.EndSessionCommand.Execute(null);

        Assert.Equal(1, relay.EndCount);
        Assert.False(viewModel.IsJoinPending);
        Assert.False(viewModel.IsError);
        Assert.Equal("Connection request cancelled.", viewModel.StatusMessage);
        Assert.False(viewModel.EndSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task DirectoryChange_AutomaticallyRefreshesAvailableHosts()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient
        {
            Capabilities = new RelayCapabilities(ServerPasswordRequired: false),
        };
        using var viewModel = new AnnotatorViewModel(service, relay);
        await viewModel.InitializeAsync();
        relay.AvailableHosts =
        [
            new AvailableHostDescriptor("new-session", "New host"),
        ];

        relay.RaiseHostDirectoryChanged();

        Assert.Equal("new-session", Assert.Single(viewModel.AvailableHosts).SessionId);
    }

    [Fact]
    public async Task ReturningAnnotatorRole_ReloadsTheListingItDroppedWhileReceiving()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient
        {
            AvailableHosts = [new AvailableHostDescriptor("peer-session", "Peer PC")],
        };
        using var viewModel = new AnnotatorViewModel(service, relay);
        await viewModel.InitializeAsync();
        Assert.Single(viewModel.AvailableHosts);

        viewModel.SetRoleEnabled(false);
        Assert.Empty(viewModel.AvailableHosts);

        viewModel.SetRoleEnabled(true);

        Assert.Equal("peer-session", Assert.Single(viewModel.AvailableHosts).SessionId);
    }

    [Fact]
    public async Task DirectoryChangeDuringASession_IsHonouredWhenTheSessionEnds()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient();
        using var viewModel = new AnnotatorViewModel(service, relay);
        await viewModel.InitializeAsync();
        relay.RaiseApproved(
            new SessionStateMessage(
                "session-1",
                true,
                new DisplayDescriptor("display", "Display", 1_920, 1_080, 1d, 0),
                DateTimeOffset.UtcNow.AddHours(1)));
        relay.AvailableHosts =
        [
            new AvailableHostDescriptor("listed-while-busy", "Late host"),
        ];

        relay.RaiseHostDirectoryChanged();
        Assert.Empty(viewModel.AvailableHosts);

        relay.RaiseSessionEnded("Disconnected from the host.");

        Assert.Equal(
            "listed-while-busy",
            Assert.Single(viewModel.AvailableHosts).SessionId);
    }

    [Fact]
    public void ApprovedPointer_IsSentAndAcknowledgementLatencyIsShown()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient();
        using var viewModel = new AnnotatorViewModel(service, relay);
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
        Assert.Contains("2560 × 1440", viewModel.HostDisplayShape, StringComparison.Ordinal);
        Assert.Contains("42 ms", viewModel.LastAcknowledgement, StringComparison.Ordinal);
    }

    [Fact]
    public void HostPause_StopsSendingAndMarksTheInputAreaPaused()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient();
        using var viewModel = new AnnotatorViewModel(service, relay);
        relay.RaiseApproved(
            new SessionStateMessage(
                "session-1",
                true,
                new DisplayDescriptor("display", "Display", 1_920, 1_080, 1d, 0),
                DateTimeOffset.UtcNow.AddHours(8)));

        relay.RaiseAnnotationPaused(true);
        service.RaisePointer(new NormalizedPoint(0.5d, 0.5d));

        Assert.True(viewModel.IsPaused);
        Assert.True(service.IsAnnotationPaused);
        Assert.Equal("Paused by host", viewModel.ConnectionStatusLabel);
        Assert.Null(relay.SentPointer);

        relay.RaiseAnnotationPaused(false);
        service.RaisePointer(new NormalizedPoint(0.5d, 0.5d));

        Assert.False(viewModel.IsPaused);
        Assert.False(service.IsAnnotationPaused);
        Assert.Equal("Connected", viewModel.ConnectionStatusLabel);
        Assert.NotNull(relay.SentPointer);
    }

    [Fact]
    public void ResumedSession_RestoresThePauseTheHostSetBeforeTheDrop()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient
        {
            Credential = new SessionCredential(
                "session-1",
                ClientRole.Annotator,
                "annotator-1",
                new string('s', 32),
                new string('r', 32),
                DateTimeOffset.UtcNow.AddHours(8)),
        };
        using var viewModel = new AnnotatorViewModel(service, relay);

        relay.RaiseApproved(
            new SessionStateMessage(
                "session-1",
                true,
                new DisplayDescriptor("display", "Display", 1_920, 1_080, 1d, 0),
                DateTimeOffset.UtcNow.AddHours(8),
                ConnectedAnnotators:
                [
                    new ConnectedAnnotatorDescriptor("Annotator", null, "annotator-1", true),
                ]));

        Assert.True(viewModel.IsPaused);
        Assert.True(service.IsAnnotationPaused);
    }

    [Fact]
    public void ReconnectingPointer_IsDroppedInsteadOfQueued()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient();
        using var viewModel = new AnnotatorViewModel(service, relay);
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
        using var viewModel = new AnnotatorViewModel(service, relay);
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
        using var viewModel = new AnnotatorViewModel(service, relay);
        relay.RaiseApproved(
            new SessionStateMessage(
                "session-1",
                true,
                new DisplayDescriptor("display", "Display", 1_920, 1_080, 1d, 0),
                DateTimeOffset.UtcNow.AddHours(8)));

        relay.RaiseSessionEnded("Ended by host.");

        Assert.False(viewModel.IsSessionApproved);
        Assert.Equal(1, service.ExitCount);
    }

    [Fact]
    public void EndSessionFailure_KeepsAnnotatorSessionActive()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient
        {
            EndException = new InvalidOperationException("Relay unavailable."),
        };
        using var viewModel = new AnnotatorViewModel(service, relay);
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
    public void CalibrateCommand_UsesSyncedHostAspectRatio()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient();
        using var viewModel = new AnnotatorViewModel(service, relay);
        relay.RaiseApproved(
            new SessionStateMessage(
                "session-1",
                true,
                new DisplayDescriptor("display", "Display", 2_560, 1_440, 1d, 0),
                DateTimeOffset.UtcNow.AddHours(8),
                HostClientInstanceId: "host-client-id"));

        viewModel.CalibrateCommand.Execute(null);

        Assert.Equal(16d / 9d, service.RequestedAspectRatio, precision: 12);
        Assert.Equal("host-client-id", service.CalibrationIdentity);
    }

    [Fact]
    public void HostDisplayChange_UpdatesShapeAndInvalidatesDifferentAspectCalibration()
    {
        using var service = new FakeTargetRegionService();
        var relay = new FakeRelayClient();
        using var viewModel = new AnnotatorViewModel(service, relay);
        relay.RaiseApproved(
            new SessionStateMessage(
                "session-1",
                true,
                new DisplayDescriptor("display", "Display", 1_920, 1_080, 1d, 0),
                DateTimeOffset.UtcNow.AddHours(8)));

        relay.RaiseHostDisplayChanged(
            new DisplayDescriptor("display", "Display", 1_200, 1_920, 1d, 90));

        Assert.Contains("1200 × 1920", viewModel.HostDisplayShape, StringComparison.Ordinal);
        Assert.Equal(1_200d / 1_920d, service.UpdatedAspectRatio, precision: 12);
    }

    [Fact]
    public void LocalDisplayChange_InvalidatesCalibration()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new AnnotatorViewModel(service);

        viewModel.HandleLocalDisplayConfigurationChanged();

        Assert.Equal(1, service.InvalidateCount);
    }

    [Fact]
    public void TogglePointingMode_OpensCalibrationWhenInactive()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new AnnotatorViewModel(service);

        viewModel.TogglePointingMode();

        Assert.Equal(1, service.ToggleCount);
        Assert.False(viewModel.IsError);
    }

    [Fact]
    public void TogglePointingMode_ForwardsWhenReady()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new AnnotatorViewModel(service);
        service.RaiseState(TargetRegionState.Ready, "Ready");

        viewModel.TogglePointingMode();

        Assert.Equal(1, service.ToggleCount);
    }

    [Fact]
    public void PointerCaptured_UpdatesCountAndCoordinates()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new AnnotatorViewModel(service);

        service.RaisePointer(new NormalizedPoint(0.25d, 0.75d));

        Assert.Equal(1, viewModel.CapturedPointerCount);
        Assert.Contains("0.2500", viewModel.LastPointer, StringComparison.Ordinal);
        Assert.Contains("0.7500", viewModel.LastPointer, StringComparison.Ordinal);
    }

    [Fact]
    public void SetUsageHintsState_ForwardsPreferencesToInputArea()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new AnnotatorViewModel(service);

        viewModel.SetUsageHintsState(showUsageHints: false, hasShownUsageHints: true);

        Assert.False(service.ShowUsageHints);
        Assert.True(service.HasShownUsageHints);
    }

    [Fact]
    public void SetDrawingOpacityPercent_ForwardsPreferenceToInputArea()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new AnnotatorViewModel(service);

        viewModel.SetDrawingOpacityPercent(35);

        Assert.Equal(35, service.DrawingOpacityPercent);
    }

    [Fact]
    public void StateChanges_UpdatePointingAndStatusProperties()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new AnnotatorViewModel(service);

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
        using var viewModel = new AnnotatorViewModel(service);

        viewModel.ReportHotKeyRegistrationFailure("Hotkey unavailable.");

        Assert.True(viewModel.IsError);
        Assert.Equal("Hotkey unavailable.", viewModel.StatusMessage);
    }

    private sealed class FakeTargetRegionService : ITargetRegionService
    {
        public event EventHandler<TargetRegionStateChangedEventArgs>? StateChanged;

        public event EventHandler<PointerCapturedEventArgs>? PointerCaptured;

        public event EventHandler? UsageHintsShown;

        public TargetRegionState State { get; private set; }

        public int BeginCalibrationCount { get; private set; }

        public double RequestedAspectRatio { get; private set; }

        public double UpdatedAspectRatio { get; private set; }

        public int InvalidateCount { get; private set; }

        public int ToggleCount { get; private set; }

        public int ExitCount { get; private set; }

        public string? CalibrationIdentity { get; private set; }

        public bool ShowUsageHints { get; private set; } = true;

        public bool HasShownUsageHints { get; private set; }

        public int DrawingOpacityPercent { get; private set; } =
            PointerSettings.DefaultDrawingOpacityPercent;

        public void SetCalibrationIdentity(string? hostIdentity) =>
            CalibrationIdentity = hostIdentity;

        public void SetUsageHintsState(bool showUsageHints, bool hasShownUsageHints)
        {
            ShowUsageHints = showUsageHints;
            HasShownUsageHints = hasShownUsageHints;
        }

        public void SetDrawingOpacityPercent(int drawingOpacityPercent) =>
            DrawingOpacityPercent = drawingOpacityPercent;

        public bool IsAnnotationPaused { get; private set; }

        public void SetAnnotationPaused(bool paused) => IsAnnotationPaused = paused;

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

        public void RaiseUsageHintsShown() => UsageHintsShown?.Invoke(this, EventArgs.Empty);

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
