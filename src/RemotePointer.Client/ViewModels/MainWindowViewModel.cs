using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Validation;

namespace RemotePointer.Client.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AsyncRelayCommand approvePresenterCommand;
    private readonly AsyncRelayCommand setReceiverAvailabilityCommand;
    private readonly IMonitorService monitorService;
    private readonly IReceiverOverlayService overlayService;
    private readonly IRelayClient? receiverRelayClient;
    private bool disposed;
    private bool isError;
    private bool isOverlayVisible;
    private bool hasConnectedPresenter;
    private bool receiverDiscoveryEnabled;
    private bool suppressAvailabilityUpdate;
    private ReceiverAvailability receiverAvailability = ReceiverAvailability.Invisible;
    private PresenterDescriptor? pendingPresenter;
    private string receiverConnectionMessage;
    private MonitorDescriptor? selectedMonitor;
    private string? receiverSessionId;
    private string statusMessage = "Select a monitor to begin.";

    public MainWindowViewModel(
        IMonitorService monitorService,
        IReceiverOverlayService overlayService,
        ITargetRegionService? targetRegionService = null,
        IRelayClient? receiverRelayClient = null,
        IRelayClient? presenterRelayClient = null,
        int pointerTtlMilliseconds = 2_000)
    {
        this.monitorService = monitorService ?? throw new ArgumentNullException(nameof(monitorService));
        this.overlayService = overlayService ?? throw new ArgumentNullException(nameof(overlayService));
        this.receiverRelayClient = receiverRelayClient;
        this.overlayService.StateChanged += OnOverlayStateChanged;
        Presenter = new PresenterViewModel(
            targetRegionService ?? new TargetRegionService(),
            presenterRelayClient,
            pointerTtlMilliseconds);

        receiverConnectionMessage = receiverRelayClient is null
            ? "Networking is not configured."
            : "Disconnected.";

        if (receiverRelayClient is not null)
        {
            receiverRelayClient.ConnectionStatusChanged += OnReceiverConnectionStatusChanged;
            receiverRelayClient.PresenterJoinRequested += OnPresenterJoinRequested;
            receiverRelayClient.SessionApproved += OnReceiverSessionApproved;
            receiverRelayClient.PointerReceived += OnPointerReceived;
            receiverRelayClient.SessionEnded += OnReceiverSessionEnded;
        }

        RefreshMonitorsCommand = new RelayCommand(_ => RefreshMonitors());
        approvePresenterCommand = new AsyncRelayCommand(
            _ => ApprovePresenterAsync(),
            _ => receiverRelayClient is not null && pendingPresenter is not null && HasReceiverSession);
        setReceiverAvailabilityCommand = new AsyncRelayCommand(
            _ => UpdateReceiverAvailabilityAsync(),
            _ => receiverRelayClient is not null
                && ReceiverDiscoveryEnabled
                && (HasReceiverSession || SelectedMonitor is not null));

        RefreshMonitors();
    }

    public ObservableCollection<MonitorDescriptor> Monitors { get; } = [];

    public PresenterViewModel Presenter { get; }

    public MonitorDescriptor? SelectedMonitor
    {
        get => selectedMonitor;
        set
        {
            if (SetProperty(ref selectedMonitor, value))
            {
                RaisePropertyChanged(nameof(CanSetReceiverAvailability));
                setReceiverAvailabilityCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsError
    {
        get => isError;
        private set => SetProperty(ref isError, value);
    }

    public bool IsOverlayVisible
    {
        get => isOverlayVisible;
        private set => SetProperty(ref isOverlayVisible, value);
    }

    public string ReceiverConnectionMessage
    {
        get => receiverConnectionMessage;
        private set => SetProperty(ref receiverConnectionMessage, value);
    }

    public string ReceiverServerUrl => receiverRelayClient?.ServerUrl ?? "Not configured";

    public string PendingPresenterName => pendingPresenter?.DisplayName ?? "No presenter waiting for approval.";

    public bool HasPendingPresenter => pendingPresenter is not null;

    public bool HasConnectedPresenter
    {
        get => hasConnectedPresenter;
        private set => SetProperty(ref hasConnectedPresenter, value);
    }

    public bool HasReceiverSession => receiverSessionId is not null;

    public bool CanSelectMonitor => !HasReceiverSession;

    public bool ReceiverDiscoveryEnabled
    {
        get => receiverDiscoveryEnabled;
        private set
        {
            if (SetProperty(ref receiverDiscoveryEnabled, value))
            {
                RaisePropertyChanged(nameof(CanSetReceiverAvailability));
                RaisePropertyChanged(nameof(ReceiverDiscoveryMessage));
                setReceiverAvailabilityCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<ReceiverAvailability> ReceiverAvailabilityOptions { get; } =
        [ReceiverAvailability.Available, ReceiverAvailability.Invisible];

    public ReceiverAvailability ReceiverAvailability
    {
        get => receiverAvailability;
        set
        {
            if (SetProperty(ref receiverAvailability, value) && !suppressAvailabilityUpdate)
            {
                setReceiverAvailabilityCommand.Execute(null);
            }
        }
    }

    public bool CanSetReceiverAvailability =>
        ReceiverDiscoveryEnabled && (HasReceiverSession || SelectedMonitor is not null);

    public string ReceiverDiscoveryMessage => ReceiverDiscoveryEnabled
        ? "Available receivers can receive access requests. Approval is still required."
        : "Receiver discovery is disabled on this relay.";

    public ICommand RefreshMonitorsCommand { get; }

    public ICommand ApprovePresenterCommand => approvePresenterCommand;

    public async Task SetReceiverAvailabilityAsync(ReceiverAvailability availability)
    {
        SetReceiverAvailabilitySilently(availability);
        await UpdateReceiverAvailabilityAsync();
    }

    public async Task InitializeAsync()
    {
        var presenterInitialization = Presenter.InitializeAsync();
        if (receiverRelayClient is not null)
        {
            try
            {
                var capabilities = await receiverRelayClient.GetRelayCapabilitiesAsync();
                ReceiverDiscoveryEnabled = capabilities.ReceiverDiscoveryEnabled;
            }
            catch (Exception exception)
            {
                SetStatus($"Relay capabilities could not be loaded: {exception.Message}", true);
            }
        }

        await presenterInitialization;
    }

    public async Task RestoreSessionsAsync()
    {
        var canRestoreReceiver = receiverRelayClient?.Credential?.Role == ClientRole.Receiver;
        var canRestorePresenter = Presenter.HasRecoverableSession;
        if (canRestoreReceiver && canRestorePresenter)
        {
            ReceiverConnectionMessage =
                "Saved receiver and presenter roles share this Windows profile; automatic recovery was skipped.";
            Presenter.ReportSharedProfileRecoverySkipped();
            return;
        }

        if (canRestoreReceiver && receiverRelayClient is not null)
        {
            _ = await receiverRelayClient.TryResumeSessionAsync();
        }

        if (canRestorePresenter)
        {
            await Presenter.RestoreSessionAsync();
        }
    }

    public void RefreshMonitors()
    {
        var previousDisplayId = SelectedMonitor?.Display.DisplayId;

        IReadOnlyList<MonitorDescriptor> refreshedMonitors;
        try
        {
            refreshedMonitors = monitorService.GetMonitors();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            Monitors.Clear();
            SelectedMonitor = null;
            SetStatus($"Displays could not be enumerated: {exception.Message}", isError: true);
            return;
        }

        Monitors.Clear();
        foreach (var monitor in refreshedMonitors)
        {
            Monitors.Add(monitor);
        }

        SelectedMonitor = previousDisplayId is null
            ? Monitors.FirstOrDefault()
            : Monitors.FirstOrDefault(
                monitor => string.Equals(
                    monitor.Display.DisplayId,
                    previousDisplayId,
                    StringComparison.OrdinalIgnoreCase));

        if (previousDisplayId is not null && SelectedMonitor is null)
        {
            if (overlayService.IsVisible)
            {
                overlayService.Hide();
            }

            SetStatus(
                "The selected monitor was disconnected. Select another monitor to continue.",
                isError: true);
            SelectedMonitor = Monitors.FirstOrDefault();
            return;
        }

        SetStatus(
            Monitors.Count == 0
                ? "No connected monitors were found."
                : $"Found {Monitors.Count} connected monitor{(Monitors.Count == 1 ? string.Empty : "s")}.",
            isError: Monitors.Count == 0);
    }

    public async Task HandleDisplayConfigurationChangedAsync()
    {
        var selectedDisplayId = SelectedMonitor?.Display.DisplayId;
        RefreshMonitors();
        Presenter.HandleLocalDisplayConfigurationChanged();

        if (receiverSessionId is null
            || receiverRelayClient is null
            || SelectedMonitor is null
            || !string.Equals(
                selectedDisplayId,
                SelectedMonitor.Display.DisplayId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await receiverRelayClient.UpdateReceiverDisplayAsync(SelectedMonitor.Display);
            SetStatus("Receiver display information updated.", false);
        }
        catch (Exception exception)
        {
            SetStatus($"Receiver display information could not be updated: {exception.Message}", true);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        overlayService.StateChanged -= OnOverlayStateChanged;
        if (receiverRelayClient is not null)
        {
            receiverRelayClient.ConnectionStatusChanged -= OnReceiverConnectionStatusChanged;
            receiverRelayClient.PresenterJoinRequested -= OnPresenterJoinRequested;
            receiverRelayClient.SessionApproved -= OnReceiverSessionApproved;
            receiverRelayClient.PointerReceived -= OnPointerReceived;
            receiverRelayClient.SessionEnded -= OnReceiverSessionEnded;
            receiverRelayClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        overlayService.Dispose();
        Presenter.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private async Task CreateReceiverSessionAsync()
    {
        if (SelectedMonitor is null || receiverRelayClient is null)
        {
            SetReceiverAvailabilitySilently(ReceiverAvailability.Invisible);
            SetStatus("Select a connected monitor before becoming available.", isError: true);
            return;
        }

        try
        {
            overlayService.Show(SelectedMonitor);
            var response = await receiverRelayClient.CreateReceiverSessionAsync(SelectedMonitor.Display);
            receiverSessionId = response.SessionId;
            SetReceiverAvailabilitySilently(ReceiverAvailability.Available);
            SetPendingPresenter(null);
            RaiseReceiverSessionProperties();
            SetStatus("This receiver is available for access requests.", false);
        }
        catch (Exception exception)
        {
            overlayService.Hide();
            ClearReceiverSession();
            SetStatus($"The receiver could not become available: {exception.Message}", true);
        }
    }

    private async Task ApprovePresenterAsync()
    {
        if (receiverRelayClient is null || receiverSessionId is null || pendingPresenter is null)
        {
            return;
        }

        try
        {
            await receiverRelayClient.ApprovePresenterAsync(
                receiverSessionId,
                pendingPresenter.ConnectionId);
            SetStatus($"Approved {pendingPresenter.DisplayName}.", false);
            SetPendingPresenter(null);
        }
        catch (Exception exception)
        {
            SetStatus($"The presenter could not be approved: {exception.Message}", true);
        }
    }

    private async Task UpdateReceiverAvailabilityAsync()
    {
        if (receiverRelayClient is null || !CanSetReceiverAvailability)
        {
            return;
        }

        var requestedAvailability = ReceiverAvailability;
        try
        {
            if (requestedAvailability == ReceiverAvailability.Available
                && !HasReceiverSession)
            {
                await CreateReceiverSessionAsync();
                return;
            }

            var isAvailable = await receiverRelayClient.SetReceiverDiscoverableAsync(
                requestedAvailability == ReceiverAvailability.Available);
            SetReceiverAvailabilitySilently(
                isAvailable ? ReceiverAvailability.Available : ReceiverAvailability.Invisible);
            SetStatus(
                isAvailable
                    ? "This receiver is available for access requests."
                    : "This receiver is invisible to presenters.",
                false);
        }
        catch (Exception exception)
        {
            SetReceiverAvailabilitySilently(
                requestedAvailability == ReceiverAvailability.Available
                    ? ReceiverAvailability.Invisible
                    : ReceiverAvailability.Available);
            SetStatus($"Receiver availability could not be changed: {exception.Message}", true);
        }
    }

    private void OnOverlayStateChanged(object? sender, OverlayStateChangedEventArgs e)
    {
        IsOverlayVisible = e.IsVisible;
        SetStatus(e.Message, e.IsError);
    }

    private void OnReceiverConnectionStatusChanged(
        object? sender,
        RelayConnectionStatusChangedEventArgs e)
    {
        ReceiverConnectionMessage = e.Message;
    }

    private void OnPresenterJoinRequested(object? sender, PresenterJoinRequestedEventArgs e)
    {
        SetPendingPresenter(e.Presenter);
        SetStatus($"{e.Presenter.DisplayName} is waiting for approval.", false);
    }

    private void OnReceiverSessionApproved(object? sender, RelaySessionStateEventArgs e)
    {
        var presenterDisconnected = HasConnectedPresenter && !e.State.Approved;
        HasConnectedPresenter = e.State.Approved;
        if (receiverRelayClient?.Credential?.Role == ClientRole.Receiver)
        {
            receiverSessionId = e.State.SessionId;
            SetReceiverAvailabilitySilently(
                e.State.ReceiverDiscoverable
                    ? ReceiverAvailability.Available
                    : ReceiverAvailability.Invisible);
            SetPendingPresenter(null);
            if (e.State.ReceiverDisplay is not null)
            {
                var restoredMonitor = Monitors.FirstOrDefault(
                    monitor => string.Equals(
                        monitor.Display.DisplayId,
                        e.State.ReceiverDisplay.DisplayId,
                        StringComparison.OrdinalIgnoreCase));
                if (restoredMonitor is null)
                {
                    RaiseReceiverSessionProperties();
                    SetStatus(
                        "Receiver presence resumed, but its monitor is not connected.",
                        true);
                    return;
                }

                SelectedMonitor = restoredMonitor;
                overlayService.Show(restoredMonitor);
            }

            RaiseReceiverSessionProperties();
        }

        SetStatus(
            e.State.Approved
                ? "Presenter approved. Incoming pointers are enabled."
                : presenterDisconnected && ReceiverAvailability == ReceiverAvailability.Available
                    ? "Presenter disconnected. This receiver remains available."
                    : presenterDisconnected
                        ? "Presenter disconnected. This receiver remains invisible."
                        : ReceiverAvailability == ReceiverAvailability.Available
                            ? "This receiver is available for access requests."
                            : "This receiver is invisible to presenters.",
            false);
    }

    private async void OnPointerReceived(object? sender, RelayPointerEventArgs e)
    {
        try
        {
            var activeSessionId = receiverSessionId;
            if (activeSessionId is null
                || !string.Equals(
                    e.PointerEvent.SessionId,
                    activeSessionId,
                    StringComparison.Ordinal))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (!ContractValidator.Validate(e.PointerEvent, now).IsValid)
            {
                return;
            }

            if (!string.Equals(
                    receiverSessionId,
                    activeSessionId,
                    StringComparison.Ordinal))
            {
                return;
            }

            var displayed = overlayService.ShowPointer(e.PointerEvent);
            if (displayed && receiverRelayClient is not null)
            {
                await receiverRelayClient.AcknowledgePointerAsync(
                    new PointerAcknowledgement(
                        e.PointerEvent.EventId,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            }
        }
        catch (Exception exception)
        {
            SetStatus($"An incoming pointer could not be displayed: {exception.Message}", true);
        }
    }

    private void OnReceiverSessionEnded(object? sender, RelaySessionEndedEventArgs e)
    {
        overlayService.Hide();
        ClearReceiverSession();
        SetStatus(e.Reason, e.Expired);
    }

    private void SetPendingPresenter(PresenterDescriptor? presenter)
    {
        pendingPresenter = presenter;
        RaisePropertyChanged(nameof(PendingPresenterName));
        RaisePropertyChanged(nameof(HasPendingPresenter));
        approvePresenterCommand.RaiseCanExecuteChanged();
    }

    private void ClearReceiverSession()
    {
        receiverSessionId = null;
        HasConnectedPresenter = false;
        SetReceiverAvailabilitySilently(ReceiverAvailability.Invisible);
        SetPendingPresenter(null);
        RaiseReceiverSessionProperties();
    }

    private void SetReceiverAvailabilitySilently(ReceiverAvailability availability)
    {
        suppressAvailabilityUpdate = true;
        ReceiverAvailability = availability;
        suppressAvailabilityUpdate = false;
    }

    private void RaiseReceiverSessionProperties()
    {
        RaisePropertyChanged(nameof(HasReceiverSession));
        RaisePropertyChanged(nameof(CanSelectMonitor));
        RaisePropertyChanged(nameof(CanSetReceiverAvailability));
        approvePresenterCommand.RaiseCanExecuteChanged();
        setReceiverAvailabilityCommand.RaiseCanExecuteChanged();
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsError = isError;
    }
}

public enum ReceiverAvailability
{
    Available,
    Invisible,
}
