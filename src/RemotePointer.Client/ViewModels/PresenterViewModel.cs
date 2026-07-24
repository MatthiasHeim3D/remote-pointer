using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.ViewModels;

public sealed class PresenterViewModel : ObservableObject, IDisposable
{
    private readonly RelayCommand calibrateCommand;
    private readonly AsyncRelayCommand endSessionCommand;
    private readonly AsyncRelayCommand joinDiscoveredReceiverCommand;
    private readonly AsyncRelayCommand refreshReceiversCommand;
    private readonly Dictionary<Guid, long> pendingAcknowledgements = [];
    private readonly int pointerTtlMilliseconds;
    private readonly IRelayClient? relayClient;
    private readonly ITargetRegionService targetRegionService;
    private readonly RelayCommand togglePointingCommand;
    private int capturedPointerCount;
    private bool disposed;
    private DisplayDescriptor? receiverDisplay;
    private AvailableReceiverDescriptor? selectedReceiver;
    private bool receiverDiscoveryEnabled;
    private bool isError;
    private bool isJoinPending;
    private bool isSessionApproved;
    private string lastAcknowledgement = "No remote marker acknowledgement yet.";
    private string lastPointer = "No local pointers captured yet.";
    private long sequenceNumber = CreateSequenceBase();
    private TargetRegionState state = TargetRegionState.Inactive;
    private string statusMessage;
    private string connectionMessage;
    private string currentReceiverName = "Connected receiver";

    public PresenterViewModel(
        ITargetRegionService targetRegionService,
        IRelayClient? relayClient = null,
        int pointerTtlMilliseconds = 2_000)
    {
        this.targetRegionService = targetRegionService
            ?? throw new ArgumentNullException(nameof(targetRegionService));
        if (pointerTtlMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pointerTtlMilliseconds));
        }

        this.relayClient = relayClient;
        this.pointerTtlMilliseconds = pointerTtlMilliseconds;
        this.targetRegionService.StateChanged += OnStateChanged;
        this.targetRegionService.PointerCaptured += OnPointerCaptured;
        statusMessage = relayClient is null
            ? "Calibrate the target area to begin."
            : "Choose a visible receiver to request access.";
        connectionMessage = relayClient is null ? "Networking is not configured." : "Disconnected.";

        if (relayClient is not null)
        {
            relayClient.ConnectionStatusChanged += OnConnectionStatusChanged;
            relayClient.SessionApproved += OnSessionApproved;
            relayClient.ReceiverDisplayChanged += OnReceiverDisplayChanged;
            relayClient.PointerDisplayed += OnPointerDisplayed;
            relayClient.SessionEnded += OnSessionEnded;
        }

        calibrateCommand = new RelayCommand(
            _ => BeginCalibration(),
            _ => relayClient is null || IsSessionApproved);
        togglePointingCommand = new RelayCommand(
            _ => TogglePointingMode(),
            _ => State is TargetRegionState.Ready or TargetRegionState.Pointing
                && (relayClient is null || IsSessionApproved));
        ExitPointingCommand = new RelayCommand(_ => targetRegionService.ExitPointingMode());
        refreshReceiversCommand = new AsyncRelayCommand(
            _ => RefreshAvailableReceiversAsync(),
            _ => relayClient is not null && ReceiverDiscoveryEnabled && !IsSessionApproved);
        joinDiscoveredReceiverCommand = new AsyncRelayCommand(
            receiver => JoinDiscoveredReceiverAsync(receiver as AvailableReceiverDescriptor),
            receiver => relayClient is not null
                && ReceiverDiscoveryEnabled
                && (receiver is AvailableReceiverDescriptor || SelectedReceiver is not null)
                && !IsJoinPending
                && !IsSessionApproved);
        endSessionCommand = new AsyncRelayCommand(
            _ => EndSessionAsync(),
            _ => relayClient is not null && IsSessionApproved);
    }

    public ObservableCollection<AvailableReceiverDescriptor> AvailableReceivers { get; } = [];

    public AvailableReceiverDescriptor? SelectedReceiver
    {
        get => selectedReceiver;
        set
        {
            if (SetProperty(ref selectedReceiver, value))
            {
                joinDiscoveredReceiverCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ReceiverDiscoveryEnabled
    {
        get => receiverDiscoveryEnabled;
        private set
        {
            if (SetProperty(ref receiverDiscoveryEnabled, value))
            {
                RaisePropertyChanged(nameof(ReceiverDiscoveryMessage));
                refreshReceiversCommand.RaiseCanExecuteChanged();
                joinDiscoveredReceiverCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ReceiverDiscoveryMessage => ReceiverDiscoveryEnabled
        ? AvailableReceivers.Count == 0
            ? "No visible receivers are currently available."
            : $"{AvailableReceivers.Count} visible receiver{(AvailableReceivers.Count == 1 ? string.Empty : "s")} available."
        : "Receiver discovery is disabled on this relay.";

    public string ReceiverDisplayShape => receiverDisplay is null
        ? "Available after receiver approval."
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{receiverDisplay.WidthPixels} × {receiverDisplay.HeightPixels} ({receiverDisplay.AspectRatio:0.###}:1)");

    public TargetRegionState State
    {
        get => state;
        private set
        {
            if (SetProperty(ref state, value))
            {
                togglePointingCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(IsPointing));
                RaisePropertyChanged(nameof(PointingActionLabel));
                RaisePropertyChanged(nameof(StateLabel));
            }
        }
    }

    public bool IsPointing => State == TargetRegionState.Pointing;

    public string PointingActionLabel => IsPointing ? "Stop pointing" : "Enable pointing";

    public string StateLabel => State.ToString();

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string ConnectionMessage
    {
        get => connectionMessage;
        private set => SetProperty(ref connectionMessage, value);
    }

    public string ServerUrl => relayClient?.ServerUrl ?? "Not configured";

    public bool HasRecoverableSession => relayClient?.Credential?.Role == ClientRole.Presenter;

    public bool IsError
    {
        get => isError;
        private set => SetProperty(ref isError, value);
    }

    public bool IsJoinPending
    {
        get => isJoinPending;
        private set
        {
            if (SetProperty(ref isJoinPending, value))
            {
                RaiseNetworkCommandStates();
            }
        }
    }

    public bool IsSessionApproved
    {
        get => isSessionApproved;
        private set
        {
            if (SetProperty(ref isSessionApproved, value))
            {
                RaiseNetworkCommandStates();
                calibrateCommand.RaiseCanExecuteChanged();
                togglePointingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CurrentReceiverName
    {
        get => currentReceiverName;
        private set => SetProperty(ref currentReceiverName, value);
    }

    public int CapturedPointerCount
    {
        get => capturedPointerCount;
        private set => SetProperty(ref capturedPointerCount, value);
    }

    public string LastPointer
    {
        get => lastPointer;
        private set => SetProperty(ref lastPointer, value);
    }

    public string LastAcknowledgement
    {
        get => lastAcknowledgement;
        private set => SetProperty(ref lastAcknowledgement, value);
    }

    public ICommand CalibrateCommand => calibrateCommand;

    public ICommand TogglePointingCommand => togglePointingCommand;

    public ICommand ExitPointingCommand { get; }

    public ICommand RefreshReceiversCommand => refreshReceiversCommand;

    public ICommand JoinDiscoveredReceiverCommand => joinDiscoveredReceiverCommand;

    public ICommand EndSessionCommand => endSessionCommand;

    public async Task InitializeAsync()
    {
        if (relayClient is null)
        {
            return;
        }

        try
        {
            var capabilities = await relayClient.GetRelayCapabilitiesAsync();
            ReceiverDiscoveryEnabled = capabilities.ReceiverDiscoveryEnabled;
            if (ReceiverDiscoveryEnabled)
            {
                await RefreshAvailableReceiversAsync();
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Relay capabilities could not be loaded: {exception.Message}", true);
        }
    }

    public async Task RestoreSessionAsync()
    {
        if (relayClient is not null)
        {
            _ = await relayClient.TryResumeSessionAsync();
        }
    }

    public void TogglePointingMode()
    {
        if (relayClient is not null && !IsSessionApproved)
        {
            SetStatus("Wait for receiver approval before enabling pointing.", isError: true);
            return;
        }

        if (State is not (TargetRegionState.Ready or TargetRegionState.Pointing))
        {
            SetStatus("Calibrate and lock a target region before enabling pointing.", isError: true);
            return;
        }

        targetRegionService.TogglePointingMode();
    }

    public void ReportHotKeyRegistrationFailure(string message) =>
        SetStatus(message, isError: true);

    public void ReportSharedProfileRecoverySkipped()
    {
        ConnectionMessage =
            "Saved receiver and presenter roles share this Windows profile; automatic recovery was skipped.";
        SetStatus("Request access to an available receiver in this client window.", false);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        targetRegionService.StateChanged -= OnStateChanged;
        targetRegionService.PointerCaptured -= OnPointerCaptured;
        if (relayClient is not null)
        {
            relayClient.ConnectionStatusChanged -= OnConnectionStatusChanged;
            relayClient.SessionApproved -= OnSessionApproved;
            relayClient.ReceiverDisplayChanged -= OnReceiverDisplayChanged;
            relayClient.PointerDisplayed -= OnPointerDisplayed;
            relayClient.SessionEnded -= OnSessionEnded;
            relayClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        targetRegionService.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private async Task RefreshAvailableReceiversAsync()
    {
        if (relayClient is null || !ReceiverDiscoveryEnabled)
        {
            return;
        }

        try
        {
            var receivers = await relayClient.GetAvailableReceiversAsync();
            var selectedSessionId = SelectedReceiver?.SessionId;
            AvailableReceivers.Clear();
            foreach (var receiver in receivers)
            {
                AvailableReceivers.Add(receiver);
            }

            SelectedReceiver = AvailableReceivers.FirstOrDefault(
                receiver => string.Equals(
                    receiver.SessionId,
                    selectedSessionId,
                    StringComparison.Ordinal))
                ?? AvailableReceivers.FirstOrDefault();
            RaisePropertyChanged(nameof(ReceiverDiscoveryMessage));
        }
        catch (Exception exception)
        {
            SetStatus($"Visible receivers could not be loaded: {exception.Message}", true);
        }
    }

    private async Task JoinDiscoveredReceiverAsync(AvailableReceiverDescriptor? receiver)
    {
        if (receiver is not null)
        {
            SelectedReceiver = receiver;
        }

        if (relayClient is null || SelectedReceiver is null)
        {
            return;
        }

        try
        {
            var requestedReceiver = SelectedReceiver;
            var response = await relayClient.RequestToJoinReceiverAsync(
                requestedReceiver.SessionId);
            HandleJoinResponse(response);
            if (response.Accepted)
            {
                CurrentReceiverName = requestedReceiver.DisplayName;
                AvailableReceivers.Remove(requestedReceiver);
                SelectedReceiver = AvailableReceivers.FirstOrDefault();
                RaisePropertyChanged(nameof(ReceiverDiscoveryMessage));
            }
        }
        catch (Exception exception)
        {
            SetStatus($"The selected receiver could not be joined: {exception.Message}", true);
        }
    }

    private void HandleJoinResponse(JoinResponse response)
    {
        if (!response.Accepted)
        {
            SetStatus(response.Reason ?? "The join request was rejected.", true);
            return;
        }

        IsJoinPending = true;
        SetStatus("Join request sent. Waiting for receiver approval.", false);
    }

    private async Task EndSessionAsync()
    {
        if (relayClient is null)
        {
            return;
        }

        targetRegionService.ExitPointingMode();
        try
        {
            await relayClient.EndSessionAsync();
            ClearSessionState();
            SetStatus("Disconnected from the receiver.", false);
        }
        catch (Exception exception)
        {
            SetStatus($"The relay could not confirm disconnection: {exception.Message}", true);
        }
    }

    private void BeginCalibration()
    {
        if (relayClient is not null && !IsSessionApproved)
        {
            SetStatus("Wait for receiver approval before calibrating.", true);
            return;
        }

        if (relayClient is not null && receiverDisplay is null)
        {
            SetStatus("Wait for the receiver display shape before calibrating.", isError: true);
            return;
        }

        targetRegionService.BeginCalibration(receiverDisplay?.AspectRatio ?? (16d / 9d));
    }

    private void OnStateChanged(object? sender, TargetRegionStateChangedEventArgs e)
    {
        State = e.State;
        SetStatus(e.Message, e.IsError);
    }

    private async void OnPointerCaptured(object? sender, PointerCapturedEventArgs e)
    {
        if (e.Kind is PointerKind.Click or PointerKind.Text or PointerKind.PathEnd
            or PointerKind.LineEnd or PointerKind.RectangleEnd)
        {
            CapturedPointerCount++;
        }

        LastPointer = string.Create(
            CultureInfo.InvariantCulture,
            $"{e.Kind}: normalized X {e.Point.X:0.0000}, Y {e.Point.Y:0.0000}");

        if (relayClient is null)
        {
            return;
        }

        var sessionId = relayClient.SessionId;
        if (!IsSessionApproved || sessionId is null)
        {
            SetStatus("Pointer dropped because there is no approved receiver connection.", true);
            return;
        }

        var sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pointerEvent = new PointerEventMessage(
            Guid.NewGuid(),
            sessionId,
            Interlocked.Increment(ref sequenceNumber),
            e.Point.X,
            e.Point.Y,
            e.Kind,
            sentAt,
            pointerTtlMilliseconds,
            e.GestureId,
            e.Text,
            e.PathPoints);
        pendingAcknowledgements[pointerEvent.EventId] = sentAt;

        try
        {
            if (!await relayClient.SendPointerAsync(pointerEvent))
            {
                pendingAcknowledgements.Remove(pointerEvent.EventId);
                SetStatus("Pointer dropped while the relay is disconnected or reconnecting.", true);
            }
        }
        catch (Exception exception)
        {
            pendingAcknowledgements.Remove(pointerEvent.EventId);
            SetStatus($"Pointer delivery failed: {exception.Message}", true);
        }

        PrunePendingAcknowledgements(sentAt);
    }

    private void OnConnectionStatusChanged(object? sender, RelayConnectionStatusChangedEventArgs e)
    {
        ConnectionMessage = e.Message;
    }

    private void OnSessionApproved(object? sender, RelaySessionStateEventArgs e)
    {
        if (!e.State.Approved)
        {
            return;
        }

        IsJoinPending = false;
        IsSessionApproved = true;
        if (e.State.ReceiverDisplay is not null)
        {
            ApplyReceiverDisplay(e.State.ReceiverDisplay);
        }

        SetStatus("Receiver approved this presenter. Calibrate the target area.", false);
    }

    private void OnReceiverDisplayChanged(
        object? sender,
        RelayReceiverDisplayChangedEventArgs e)
    {
        ApplyReceiverDisplay(e.Display);
        SetStatus("The receiver display changed. Review or repeat calibration.", false);
    }

    public void HandleLocalDisplayConfigurationChanged() =>
        targetRegionService.InvalidateCalibration(
            "The local display configuration changed. Recalibrate the target area.");

    private void ApplyReceiverDisplay(DisplayDescriptor display)
    {
        receiverDisplay = display;
        RaisePropertyChanged(nameof(ReceiverDisplayShape));
        targetRegionService.UpdateExpectedAspectRatio(display.AspectRatio);
    }

    private void OnPointerDisplayed(object? sender, RelayAcknowledgementEventArgs e)
    {
        if (!pendingAcknowledgements.Remove(e.Acknowledgement.EventId, out var sentAt))
        {
            return;
        }

        var latency = Math.Max(0, e.Acknowledgement.DisplayedAtUnixMilliseconds - sentAt);
        LastAcknowledgement = string.Create(
            CultureInfo.InvariantCulture,
            $"Receiver displayed the marker in {latency} ms.");
    }

    private void OnSessionEnded(object? sender, RelaySessionEndedEventArgs e)
    {
        targetRegionService.ExitPointingMode();
        ClearSessionState();
        SetStatus(e.Reason, e.Expired);
    }

    private void ClearSessionState()
    {
        IsJoinPending = false;
        IsSessionApproved = false;
        receiverDisplay = null;
        CurrentReceiverName = "Connected receiver";
        RaisePropertyChanged(nameof(ReceiverDisplayShape));
        pendingAcknowledgements.Clear();
        sequenceNumber = Math.Max(sequenceNumber, CreateSequenceBase());
    }

    private void RaiseNetworkCommandStates()
    {
        refreshReceiversCommand.RaiseCanExecuteChanged();
        joinDiscoveredReceiverCommand.RaiseCanExecuteChanged();
        endSessionCommand.RaiseCanExecuteChanged();
    }

    private void PrunePendingAcknowledgements(long now)
    {
        if (pendingAcknowledgements.Count <= 256)
        {
            return;
        }

        var cutoff = now - pointerTtlMilliseconds;
        foreach (var staleEventId in pendingAcknowledgements
                     .Where(pair => pair.Value < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            pendingAcknowledgements.Remove(staleEventId);
        }
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsError = isError;
    }

    private static long CreateSequenceBase() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_024L;
}
