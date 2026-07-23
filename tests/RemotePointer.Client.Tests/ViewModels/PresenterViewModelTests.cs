using RemotePointer.Client.Services;
using RemotePointer.Client.Tests.Fakes;
using RemotePointer.Client.ViewModels;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Tests.ViewModels;

public sealed class PresenterViewModelTests
{
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
        Assert.Equal(2_560d, viewModel.ExpectedWidthPixels);
        Assert.Equal(1_440d, viewModel.ExpectedHeightPixels);
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
    public void CalibrateCommand_SendsExpectedAspectRatioAndLockPreference()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new PresenterViewModel(service)
        {
            ExpectedWidthPixels = 2_560d,
            ExpectedHeightPixels = 1_440d,
            AspectRatioLockEnabled = true,
        };

        viewModel.CalibrateCommand.Execute(null);

        Assert.Equal(16d / 9d, service.RequestedAspectRatio, precision: 12);
        Assert.True(service.RequestedAspectLock);
    }

    [Theory]
    [InlineData(0d, 1_080d)]
    [InlineData(1_920d, -1d)]
    [InlineData(double.NaN, 1_080d)]
    public void CalibrateCommand_RejectsInvalidExpectedDimensions(double width, double height)
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new PresenterViewModel(service)
        {
            ExpectedWidthPixels = width,
            ExpectedHeightPixels = height,
        };

        viewModel.CalibrateCommand.Execute(null);

        Assert.Equal(0, service.BeginCalibrationCount);
        Assert.True(viewModel.IsError);
    }

    [Fact]
    public void TogglePointingMode_RequiresLockedCalibration()
    {
        using var service = new FakeTargetRegionService();
        using var viewModel = new PresenterViewModel(service);

        viewModel.TogglePointingMode();

        Assert.Equal(0, service.ToggleCount);
        Assert.True(viewModel.IsError);
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

        public bool RequestedAspectLock { get; private set; }

        public int ToggleCount { get; private set; }

        public int ExitCount { get; private set; }

        public void BeginCalibration(double expectedAspectRatio, bool lockAspectRatio)
        {
            BeginCalibrationCount++;
            RequestedAspectRatio = expectedAspectRatio;
            RequestedAspectLock = lockAspectRatio;
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

        public void RaisePointer(NormalizedPoint point) =>
            PointerCaptured?.Invoke(this, new PointerCapturedEventArgs(point));

        public void Dispose()
        {
        }
    }
}
