using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.ViewModels;

public sealed class AnnotatorViewModel : ObservableObject, IDisposable
{
    private readonly RelayCommand calibrateCommand;
    private readonly AsyncRelayCommand endSessionCommand;
    private readonly AsyncRelayCommand joinDiscoveredHostCommand;
    private readonly AsyncRelayCommand refreshHostsCommand;
    private readonly Dictionary<Guid, long> pendingAcknowledgements = [];
    private readonly int pointerTtlMilliseconds;
    private readonly IRelayClient? relayClient;
    private readonly ITargetRegionService targetRegionService;
    private readonly RelayCommand togglePointingCommand;
    private int capturedPointerCount;
    private bool directoryReadPending;
    private bool disposed;
    private bool isReadingDirectory;
    private DisplayDescriptor? hostDisplay;
    private AvailableHostDescriptor? selectedHost;
    private bool isError;
    private bool isJoinPending;
    private bool isPaused;
    private bool isSessionApproved;
    private bool senderRoleEnabled = true;
    private string lastAcknowledgement = "No remote marker acknowledgement yet.";
    private string lastPointer = "No local pointers captured yet.";
    private long sequenceNumber = CreateSequenceBase();
    private TargetRegionState state = TargetRegionState.Inactive;
    private string statusMessage;
    private string connectionMessage;
    private string currentHostName = "Connected host";
    private byte[]? currentHostProfilePicturePng;

    public AnnotatorViewModel(
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
            : "Choose a visible host to request access.";
        connectionMessage = relayClient is null ? "Networking is not configured." : "Disconnected.";

        if (relayClient is not null)
        {
            relayClient.ConnectionStatusChanged += OnConnectionStatusChanged;
            relayClient.SessionApproved += OnSessionApproved;
            relayClient.HostDisplayChanged += OnHostDisplayChanged;
            relayClient.PointerDisplayed += OnPointerDisplayed;
            relayClient.SessionEnded += OnSessionEnded;
            relayClient.HostDirectoryChanged += OnHostDirectoryChanged;
            relayClient.AnnotationPausedChanged += OnAnnotationPausedChanged;
        }

        calibrateCommand = new RelayCommand(
            _ => BeginCalibration(),
            _ => relayClient is null || IsSessionApproved);
        togglePointingCommand = new RelayCommand(
            _ => TogglePointingMode(),
            _ => State != TargetRegionState.Calibrating
                && (relayClient is null || IsSessionApproved));
        ExitPointingCommand = new RelayCommand(_ => targetRegionService.ExitPointingMode());
        refreshHostsCommand = new AsyncRelayCommand(
            _ => RefreshAvailableHostsAsync(),
            _ => relayClient is not null
                && RoleEnabled
                && !IsSessionApproved);
        joinDiscoveredHostCommand = new AsyncRelayCommand(
            host => JoinDiscoveredHostAsync(host as AvailableHostDescriptor),
            host => relayClient is not null
                && RoleEnabled
                && (host is AvailableHostDescriptor || SelectedHost is not null)
                && !IsJoinPending
                && !IsSessionApproved);
        endSessionCommand = new AsyncRelayCommand(
            _ => EndSessionAsync(),
            _ => relayClient is not null && (IsSessionApproved || IsJoinPending));
    }

    public ObservableCollection<AvailableHostDescriptor> AvailableHosts { get; } = [];

    public AvailableHostDescriptor? SelectedHost
    {
        get => selectedHost;
        set
        {
            if (SetProperty(ref selectedHost, value))
            {
                joinDiscoveredHostCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool RoleEnabled
    {
        get => senderRoleEnabled;
        private set
        {
            if (SetProperty(ref senderRoleEnabled, value))
            {
                RaiseNetworkCommandStates();
            }
        }
    }

    public string HostDiscoveryMessage => AvailableHosts.Count == 0
        ? "No visible hosts are currently available."
        : $"{AvailableHosts.Count} visible host{(AvailableHosts.Count == 1 ? string.Empty : "s")} available.";

    public string HostDisplayShape => hostDisplay is null
        ? "Available after host approval."
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{hostDisplay.WidthPixels} × {hostDisplay.HeightPixels} ({hostDisplay.AspectRatio:0.###}:1)");

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
                RaisePropertyChanged(nameof(PointingActionIcon));
                RaisePropertyChanged(nameof(StateLabel));
            }
        }
    }

    public bool IsPointing => State == TargetRegionState.Pointing;

    public string PointingActionLabel => IsPointing ? "Stop pointing" : "Enable pointing";

    public string PointingActionIcon => IsPointing ? "\uE71A" : "\uE768";

    /// <summary>
    /// True while the host has this annotator paused. The session stays up and the target region
    /// stays calibrated; the input it captures simply goes nowhere until the host lifts it.
    /// </summary>
    public bool IsPaused
    {
        get => isPaused;
        private set
        {
            if (SetProperty(ref isPaused, value))
            {
                RaisePropertyChanged(nameof(ConnectionStatusLabel));
                targetRegionService.SetAnnotationPaused(value);
            }
        }
    }

    public string ConnectionStatusLabel => IsSessionApproved
        ? IsPaused ? "Paused by host" : "Connected"
        : "Request sent. Waiting for approval.";

    public string EndSessionActionLabel => IsSessionApproved
        ? "Disconnect from host"
        : "Cancel connection request";

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

    public bool HasRecoverableSession => relayClient?.Credential?.Role == ClientRole.Annotator;

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
                RaisePropertyChanged(nameof(ConnectionStatusLabel));
                RaisePropertyChanged(nameof(EndSessionActionLabel));
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
                RaisePropertyChanged(nameof(ConnectionStatusLabel));
                RaisePropertyChanged(nameof(EndSessionActionLabel));
            }
        }
    }

    public string CurrentHostName
    {
        get => currentHostName;
        private set => SetProperty(ref currentHostName, value);
    }

    public byte[]? CurrentHostProfilePicturePng
    {
        get => currentHostProfilePicturePng;
        private set => SetProperty(ref currentHostProfilePicturePng, value);
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

    public ICommand RefreshHostsCommand => refreshHostsCommand;

    public ICommand JoinDiscoveredHostCommand => joinDiscoveredHostCommand;

    public ICommand EndSessionCommand => endSessionCommand;

    // The directory is the only thing this view model ever read from relay capabilities, so it
    // goes straight to the listing and skips the extra round trip on every start.
    public Task InitializeAsync() =>
        relayClient is null || !RoleEnabled
            ? Task.CompletedTask
            : RefreshAvailableHostsAsync();

    /// <summary>
    /// Follows the host role: this client cannot send while it is receiving, so the listing
    /// is dropped while the annotator role is off and read again as soon as it comes back. Without
    /// that read the listing would stay empty after the last annotator disconnects, because the
    /// relay only announces what changed in the directory and nothing there did.
    /// </summary>
    public void SetRoleEnabled(bool enabled)
    {
        RoleEnabled = enabled;
        if (!enabled)
        {
            AvailableHosts.Clear();
            SelectedHost = null;
            RaisePropertyChanged(nameof(HostDiscoveryMessage));
            return;
        }

        RequestDirectoryRead();
    }

    /// <summary>
    /// The listing this view model shows belongs to the old password until the relay is told
    /// otherwise, so the key goes out first and the directory is read again behind it.
    /// </summary>
    public async Task SetServerPasswordKeyAsync(string? key)
    {
        if (relayClient is null)
        {
            return;
        }

        await relayClient.SetServerPasswordKeyAsync(key);
        await RefreshAvailableHostsAsync();
    }

    public void SetUsageHintsState(bool showUsageHints, bool hasShownUsageHints) =>
        targetRegionService.SetUsageHintsState(showUsageHints, hasShownUsageHints);

    public void SetDrawingOpacityPercent(int drawingOpacityPercent) =>
        targetRegionService.SetDrawingOpacityPercent(drawingOpacityPercent);

    public Task ApplyClientSettingsAsync(
        string displayName,
        string? profilePicturePath,
        int maximumAnnotatorConnections) =>
        relayClient?.ApplyClientSettingsAsync(
            displayName,
            profilePicturePath,
            maximumAnnotatorConnections)
        ?? Task.CompletedTask;

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
            SetStatus("Wait for host approval before enabling pointing.", isError: true);
            return;
        }

        targetRegionService.TogglePointingMode();
    }

    public void ReportHotKeyRegistrationFailure(string message) =>
        SetStatus(message, isError: true);

    public void ReportSharedProfileRecoverySkipped()
    {
        ConnectionMessage =
            "Saved host and annotator roles share this Windows profile; automatic recovery was skipped.";
        SetStatus("Request access to an available host in this client window.", false);
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
            relayClient.HostDisplayChanged -= OnHostDisplayChanged;
            relayClient.PointerDisplayed -= OnPointerDisplayed;
            relayClient.SessionEnded -= OnSessionEnded;
            relayClient.HostDirectoryChanged -= OnHostDirectoryChanged;
            relayClient.AnnotationPausedChanged -= OnAnnotationPausedChanged;
            RelayClientShutdown.Complete(relayClient);
        }

        targetRegionService.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// True while this client both wants a listing and is allowed one. A session owns the panel
    /// while it lasts, and the host role turns the annotator off entirely, so a read is
    /// pointless in either state — but the directory keeps moving underneath, which is why a
    /// read that cannot run now is remembered rather than skipped.
    /// </summary>
    private bool CanReadDirectory =>
        relayClient is not null && RoleEnabled && !IsSessionApproved && !IsJoinPending;

    /// <summary>
    /// Records that the listing is stale and reads it when that is possible. Every notification
    /// the relay sends lands here, including the ones that arrive while a session holds the
    /// panel: those are honoured the moment it is released, so the listing never depends on a
    /// notification arriving in a particular order relative to the session state it describes.
    /// </summary>
    private void RequestDirectoryRead()
    {
        directoryReadPending = true;
        _ = ReadDirectoryAsync();
    }

    private Task RefreshAvailableHostsAsync()
    {
        directoryReadPending = true;
        return ReadDirectoryAsync();
    }

    /// <summary>
    /// Drains the pending read, one call at a time. Overlapping reads would race to publish
    /// their snapshots and the older one could win, so a read that arrives while another is in
    /// flight only marks the listing stale again and lets the running loop pick it up.
    /// </summary>
    private async Task ReadDirectoryAsync()
    {
        if (isReadingDirectory)
        {
            return;
        }

        isReadingDirectory = true;
        try
        {
            while (directoryReadPending && CanReadDirectory)
            {
                directoryReadPending = false;
                await ReadAvailableHostsAsync();
            }
        }
        finally
        {
            isReadingDirectory = false;
        }
    }

    private async Task ReadAvailableHostsAsync()
    {
        try
        {
            var hosts = await relayClient!.GetAvailableHostsAsync();
            var selectedSessionId = SelectedHost?.SessionId;
            AvailableHosts.Clear();
            foreach (var host in hosts)
            {
                AvailableHosts.Add(host);
            }

            SelectedHost = AvailableHosts.FirstOrDefault(
                host => string.Equals(
                    host.SessionId,
                    selectedSessionId,
                    StringComparison.Ordinal))
                ?? AvailableHosts.FirstOrDefault();
            RaisePropertyChanged(nameof(HostDiscoveryMessage));
        }
        catch (Exception exception)
        {
            SetStatus($"Visible hosts could not be loaded: {exception.Message}", true);
        }
    }

    private void OnHostDirectoryChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        RequestDirectoryRead();
    }

    private async Task JoinDiscoveredHostAsync(AvailableHostDescriptor? host)
    {
        if (host is not null)
        {
            SelectedHost = host;
        }

        if (relayClient is null || SelectedHost is null)
        {
            return;
        }

        try
        {
            var requestedHost = SelectedHost;
            var response = await relayClient.RequestToJoinHostAsync(
                requestedHost.SessionId);
            if (response.Accepted)
            {
                CurrentHostName = requestedHost.DisplayName;
                CurrentHostProfilePicturePng = requestedHost.ProfilePicturePng is null
                    ? null
                    : [.. requestedHost.ProfilePicturePng];
                AvailableHosts.Remove(requestedHost);
                SelectedHost = AvailableHosts.FirstOrDefault();
                RaisePropertyChanged(nameof(HostDiscoveryMessage));
            }
            HandleJoinResponse(response);
        }
        catch (Exception exception)
        {
            SetStatus($"The selected host could not be joined: {exception.Message}", true);
        }
    }

    private void HandleJoinResponse(JoinResponse response)
    {
        if (!response.Accepted)
        {
            // A refused request usually means the listing was already out of date — the
            // host went invisible, filled up, or took another request first.
            RequestDirectoryRead();
            SetStatus(response.Reason ?? "The join request was rejected.", true);
            return;
        }

        IsJoinPending = true;
        SetStatus("Join request sent. Waiting for host approval.", false);
    }

    private async Task EndSessionAsync()
    {
        if (relayClient is null)
        {
            return;
        }

        var wasApproved = IsSessionApproved;
        targetRegionService.ExitPointingMode();
        try
        {
            await relayClient.EndSessionAsync();
            ClearSessionState();
            SetStatus(
                wasApproved
                    ? "Disconnected from the host."
                    : "Connection request cancelled.",
                false);
        }
        catch (Exception exception)
        {
            SetStatus(
                wasApproved
                    ? $"The relay could not confirm disconnection: {exception.Message}"
                    : $"The connection request could not be cancelled: {exception.Message}",
                true);
        }
    }

    private void BeginCalibration()
    {
        if (relayClient is not null && !IsSessionApproved)
        {
            SetStatus("Wait for host approval before calibrating.", true);
            return;
        }

        if (relayClient is not null && hostDisplay is null)
        {
            SetStatus("Wait for the host display shape before calibrating.", isError: true);
            return;
        }

        targetRegionService.BeginCalibration(hostDisplay?.AspectRatio ?? (16d / 9d));
    }

    private void OnStateChanged(object? sender, TargetRegionStateChangedEventArgs e)
    {
        State = e.State;
        SetStatus(e.Message, e.IsError);
    }

    private async void OnPointerCaptured(object? sender, PointerCapturedEventArgs e)
    {
        if (e.Kind is PointerKind.Click or PointerKind.Text or PointerKind.PathEnd
            or PointerKind.LineEnd or PointerKind.RectangleEnd or PointerKind.CircleEnd)
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
            SetStatus("Pointer dropped because there is no approved host connection.", true);
            return;
        }

        if (IsPaused)
        {
            // The relay drops these anyway; not sending keeps a paused annotator off the wire.
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
        if (e.Status == RelayConnectionStatus.Connected)
        {
            // A connection that dropped missed every notification sent while it was away, so
            // the listing it carries is only a guess until it is read again.
            RequestDirectoryRead();
        }
    }

    private void OnSessionApproved(object? sender, RelaySessionStateEventArgs e)
    {
        if (!e.State.Approved)
        {
            return;
        }

        IsJoinPending = false;
        IsSessionApproved = true;
        // A resumed session carries the pause the host set before the connection dropped, and
        // the relay repeats it only in this state message.
        IsPaused = FindOwnEntry(e.State)?.IsPaused ?? IsPaused;
        CurrentHostName = e.State.HostDisplayName ?? CurrentHostName;
        CurrentHostProfilePicturePng = e.State.HostProfilePicturePng
            is null ? null : [.. e.State.HostProfilePicturePng];
        targetRegionService.SetCalibrationIdentity(
            e.State.HostClientInstanceId ?? CurrentHostName);
        if (e.State.HostDisplay is not null)
        {
            ApplyHostDisplay(e.State.HostDisplay);
        }

        SetStatus("Host approved this annotator. Calibrate the target area.", false);
    }

    private ConnectedAnnotatorDescriptor? FindOwnEntry(SessionStateMessage state)
    {
        var ownId = relayClient?.Credential?.ClientInstanceId;
        return string.IsNullOrEmpty(ownId)
            ? null
            : (state.ConnectedAnnotators ?? []).FirstOrDefault(
                annotator => string.Equals(
                    annotator.AnnotatorId,
                    ownId,
                    StringComparison.Ordinal));
    }

    private void OnAnnotationPausedChanged(object? sender, RelayAnnotationPausedEventArgs e)
    {
        _ = sender;
        IsPaused = e.Paused;
        SetStatus(
            e.Paused
                ? "The host paused this annotator. Your input is not being sent."
                : "The host resumed this annotator. Your input is being sent again.",
            false);
    }

    private void OnHostDisplayChanged(
        object? sender,
        RelayHostDisplayChangedEventArgs e)
    {
        ApplyHostDisplay(e.Display);
        SetStatus("The host display changed. Review or repeat calibration.", false);
    }

    public void HandleLocalDisplayConfigurationChanged() =>
        targetRegionService.InvalidateCalibration(
            "The local display configuration changed. Recalibrate the target area.");

    private void ApplyHostDisplay(DisplayDescriptor display)
    {
        hostDisplay = display;
        RaisePropertyChanged(nameof(HostDisplayShape));
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
            $"Host displayed the marker in {latency} ms.");
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
        IsPaused = false;
        targetRegionService.SetCalibrationIdentity(null);
        hostDisplay = null;
        CurrentHostName = "Connected host";
        CurrentHostProfilePicturePng = null;
        RaisePropertyChanged(nameof(HostDisplayShape));
        pendingAcknowledgements.Clear();
        sequenceNumber = Math.Max(sequenceNumber, CreateSequenceBase());
        // The panel shows the listing again from here, and it went unread for the whole
        // session, so it is read now rather than waiting for the next thing to change.
        RequestDirectoryRead();
    }

    private void RaiseNetworkCommandStates()
    {
        refreshHostsCommand.RaiseCanExecuteChanged();
        joinDiscoveredHostCommand.RaiseCanExecuteChanged();
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
