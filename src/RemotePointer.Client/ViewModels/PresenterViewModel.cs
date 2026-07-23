using System.Globalization;
using System.Windows.Input;
using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Validation;

namespace RemotePointer.Client.ViewModels;

public sealed class PresenterViewModel : ObservableObject, IDisposable
{
    private readonly RelayCommand calibrateCommand;
    private readonly AsyncRelayCommand endSessionCommand;
    private readonly AsyncRelayCommand joinSessionCommand;
    private readonly Dictionary<Guid, long> pendingAcknowledgements = [];
    private readonly int pointerTtlMilliseconds;
    private readonly IRelayClient? relayClient;
    private readonly ITargetRegionService targetRegionService;
    private readonly RelayCommand togglePointingCommand;
    private bool aspectRatioLockEnabled = true;
    private int capturedPointerCount;
    private bool disposed;
    private double expectedHeightPixels = 1_080d;
    private double expectedWidthPixels = 1_920d;
    private bool isError;
    private bool isJoinPending;
    private bool isSessionApproved;
    private string lastAcknowledgement = "No remote marker acknowledgement yet.";
    private string lastPointer = "No local pointers captured yet.";
    private string pairingCode = string.Empty;
    private long sequenceNumber = CreateSequenceBase();
    private TargetRegionState state = TargetRegionState.Inactive;
    private string statusMessage;
    private string connectionMessage;

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
            : "Enter the receiver's pairing code to begin.";
        connectionMessage = relayClient is null ? "Networking is not configured." : "Disconnected.";

        if (relayClient is not null)
        {
            relayClient.ConnectionStatusChanged += OnConnectionStatusChanged;
            relayClient.SessionApproved += OnSessionApproved;
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
        joinSessionCommand = new AsyncRelayCommand(
            _ => JoinSessionAsync(),
            _ => relayClient is not null && !IsJoinPending && !IsSessionApproved);
        endSessionCommand = new AsyncRelayCommand(
            _ => EndSessionAsync(),
            _ => relayClient is not null && IsSessionApproved);
    }

    public double ExpectedWidthPixels
    {
        get => expectedWidthPixels;
        set => SetProperty(ref expectedWidthPixels, value);
    }

    public double ExpectedHeightPixels
    {
        get => expectedHeightPixels;
        set => SetProperty(ref expectedHeightPixels, value);
    }

    public bool AspectRatioLockEnabled
    {
        get => aspectRatioLockEnabled;
        set => SetProperty(ref aspectRatioLockEnabled, value);
    }

    public string PairingCode
    {
        get => pairingCode;
        set => SetProperty(ref pairingCode, value);
    }

    public TargetRegionState State
    {
        get => state;
        private set
        {
            if (SetProperty(ref state, value))
            {
                togglePointingCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(IsPointing));
                RaisePropertyChanged(nameof(StateLabel));
            }
        }
    }

    public bool IsPointing => State == TargetRegionState.Pointing;

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

    public ICommand JoinSessionCommand => joinSessionCommand;

    public ICommand EndSessionCommand => endSessionCommand;

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
        SetStatus("Create or join a new session for this client window.", false);
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
            relayClient.PointerDisplayed -= OnPointerDisplayed;
            relayClient.SessionEnded -= OnSessionEnded;
            relayClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        targetRegionService.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private async Task JoinSessionAsync()
    {
        if (relayClient is null)
        {
            return;
        }

        if (!PairingCodeValidator.IsValid(PairingCode))
        {
            SetStatus("Enter the six-character pairing code shown by the receiver.", true);
            return;
        }

        try
        {
            var response = await relayClient.RequestToJoinSessionAsync(
                PairingCodeValidator.Normalize(PairingCode));
            if (!response.Accepted)
            {
                SetStatus(response.Reason ?? "The join request was rejected.", true);
                return;
            }

            IsJoinPending = true;
            SetStatus("Join request sent. Waiting for receiver approval.", false);
        }
        catch (Exception exception)
        {
            SetStatus($"The receiver session could not be joined: {exception.Message}", true);
        }
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
            SetStatus("Presenter session ended.", false);
        }
        catch (Exception exception)
        {
            SetStatus($"The relay could not confirm session termination: {exception.Message}", true);
        }
    }

    private void BeginCalibration()
    {
        if (relayClient is not null && !IsSessionApproved)
        {
            SetStatus("Wait for receiver approval before calibrating.", true);
            return;
        }

        if (!double.IsFinite(ExpectedWidthPixels)
            || !double.IsFinite(ExpectedHeightPixels)
            || ExpectedWidthPixels <= 0d
            || ExpectedHeightPixels <= 0d)
        {
            SetStatus("Expected receiver dimensions must be positive numbers.", isError: true);
            return;
        }

        var expectedRatio = AspectRatio.Calculate(
            ExpectedWidthPixels,
            ExpectedHeightPixels);
        targetRegionService.BeginCalibration(expectedRatio, AspectRatioLockEnabled);
    }

    private void OnStateChanged(object? sender, TargetRegionStateChangedEventArgs e)
    {
        State = e.State;
        SetStatus(e.Message, e.IsError);
    }

    private async void OnPointerCaptured(object? sender, PointerCapturedEventArgs e)
    {
        CapturedPointerCount++;
        LastPointer = string.Create(
            CultureInfo.InvariantCulture,
            $"Normalized X {e.Point.X:0.0000}, Y {e.Point.Y:0.0000}");

        if (relayClient is null)
        {
            return;
        }

        var sessionId = relayClient.SessionId;
        if (!IsSessionApproved || sessionId is null)
        {
            SetStatus("Pointer dropped because there is no approved session.", true);
            return;
        }

        var sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pointerEvent = new PointerEventMessage(
            Guid.NewGuid(),
            sessionId,
            Interlocked.Increment(ref sequenceNumber),
            e.Point.X,
            e.Point.Y,
            PointerKind.Click,
            sentAt,
            pointerTtlMilliseconds);
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
            ExpectedWidthPixels = e.State.ReceiverDisplay.WidthPixels;
            ExpectedHeightPixels = e.State.ReceiverDisplay.HeightPixels;
        }

        SetStatus("Receiver approved this presenter. Calibrate the target area.", false);
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
        pendingAcknowledgements.Clear();
        sequenceNumber = Math.Max(sequenceNumber, CreateSequenceBase());
    }

    private void RaiseNetworkCommandStates()
    {
        joinSessionCommand.RaiseCanExecuteChanged();
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
