using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using RemotePointer.Client.Configuration;
using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Validation;

namespace RemotePointer.Client.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AsyncRelayCommand applyServerPasswordCommand;
    private readonly AsyncRelayCommand approvePresenterCommand;
    private readonly SemaphoreSlim availabilityUpdateGate = new(1, 1);
    private readonly RelayCommand changeServerPasswordCommand;
    private readonly AsyncRelayCommand disconnectAllConnectionsCommand;
    private readonly AsyncRelayCommand setReceiverAvailabilityCommand;
    private readonly AsyncRelayCommand testServerConnectionCommand;
    private readonly IMonitorService monitorService;
    private readonly IReceiverOverlayService overlayService;
    private readonly IRelayClient? receiverRelayClient;
    private readonly ClientSettings? clientSettings;
    private readonly IStartupRegistrationService? startupRegistrationService;
    private readonly IServerConnectionTester serverConnectionTester;
    private readonly IServerPasswordStore? serverPasswordStore;
    private readonly ITargetRegionService targetRegionService;
    private readonly RelayCommand decrementMaximumSendersCommand;
    private readonly RelayCommand incrementMaximumSendersCommand;
    private CancellationTokenSource? availabilityRetryCancellation;
    private bool disposed;
    private bool isError;
    private bool isOverlayVisible;
    private bool hasConnectedPresenter;
    private ReceiverAvailability receiverAvailability;
    private PresenterDescriptor? pendingPresenter;
    private string receiverConnectionMessage;
    private MonitorDescriptor? selectedMonitor;
    private string? receiverSessionId;
    private string statusMessage = "Select a monitor to begin.";
    private bool isAvailabilityMenuOpen;
    private bool isSettingsOpen;
    private string profilePicturePath;
    private string serverAddressInput;
    private string userName;
    private int maximumSenderConnections;
    private bool isLaunchAtStartup;
    private bool showUsageHints = true;
    private bool hasShownUsageHints;
    private bool isServerAddressVerified;
    private bool hasServerPassword;
    private bool isChangingServerPassword;
    private bool serverPasswordRequired;
    private string serverPasswordInput = string.Empty;
    private bool pendingRelayReinitialization;
    private string? lastTestedServerAddress;
    private bool lastServerConnectionTestSucceeded;
    private string serverConnectionTestMessage = string.Empty;
    private readonly string activeServerAddress;

    public event EventHandler<ServerAddressChangeRequestedEventArgs>? ServerAddressChangeRequested;

    public event EventHandler? RelayReinitializationRequested;

    public MainWindowViewModel(
        IMonitorService monitorService,
        IReceiverOverlayService overlayService,
        ITargetRegionService? targetRegionService = null,
        IRelayClient? receiverRelayClient = null,
        IRelayClient? presenterRelayClient = null,
        int pointerTtlMilliseconds = 2_000,
        ClientSettings? clientSettings = null,
        IStartupRegistrationService? startupRegistrationService = null,
        IServerConnectionTester? serverConnectionTester = null,
        IServerPasswordStore? serverPasswordStore = null)
    {
        this.monitorService = monitorService ?? throw new ArgumentNullException(nameof(monitorService));
        this.overlayService = overlayService ?? throw new ArgumentNullException(nameof(overlayService));
        this.receiverRelayClient = receiverRelayClient;
        this.clientSettings = clientSettings;
        this.startupRegistrationService = startupRegistrationService;
        this.serverConnectionTester = serverConnectionTester ?? new ServerConnectionTester();
        this.serverPasswordStore = serverPasswordStore;
        hasServerPassword = !string.IsNullOrWhiteSpace(clientSettings?.Server.PasswordKey);
        var configuredServerAddress = clientSettings?.Server.BaseUrl
            ?? receiverRelayClient?.ServerUrl
            ?? string.Empty;
        activeServerAddress = configuredServerAddress;
        serverAddressInput = RemoveHttpsPrefix(configuredServerAddress);
        userName = clientSettings?.Profile.UserName ?? Environment.UserName;
        profilePicturePath = clientSettings?.Profile.PicturePath ?? string.Empty;
        maximumSenderConnections = clientSettings?.Receiver.MaximumSenderConnections ?? 2;
        receiverAvailability = clientSettings?.Receiver.IsAvailable == true
            ? ReceiverAvailability.Available
            : ReceiverAvailability.Invisible;
        isLaunchAtStartup = startupRegistrationService?.IsEnabled
            ?? clientSettings?.Startup.LaunchAtStartup
            ?? false;
        showUsageHints = clientSettings?.Pointer.ShowUsageHints ?? true;
        hasShownUsageHints = clientSettings?.Pointer.HasShownUsageHints ?? false;
        this.overlayService.StateChanged += OnOverlayStateChanged;
        this.targetRegionService = targetRegionService ?? new TargetRegionService();
        this.targetRegionService.UsageHintsShown += OnUsageHintsShown;
        Presenter = new PresenterViewModel(
            this.targetRegionService,
            presenterRelayClient,
            pointerTtlMilliseconds);
        Presenter.SetUsageHintsState(showUsageHints, hasShownUsageHints);
        Presenter.PropertyChanged += OnPresenterPropertyChanged;

        receiverConnectionMessage = receiverRelayClient is null
            ? "Networking is not configured."
            : "Disconnected.";

        if (receiverRelayClient is not null)
        {
            receiverRelayClient.ConnectionStatusChanged += OnReceiverConnectionStatusChanged;
            receiverRelayClient.PresenterJoinRequested += OnPresenterJoinRequested;
            receiverRelayClient.PresenterJoinCancelled += OnPresenterJoinCancelled;
            receiverRelayClient.SessionApproved += OnReceiverSessionApproved;
            receiverRelayClient.PointerReceived += OnPointerReceived;
            receiverRelayClient.SessionEnded += OnReceiverSessionEnded;
        }

        RefreshMonitorsCommand = new RelayCommand(_ => RefreshMonitors());
        approvePresenterCommand = new AsyncRelayCommand(
            _ => ApprovePendingPresenterAsync(),
            _ => receiverRelayClient is not null
                && pendingPresenter is not null
                && HasReceiverSession
                && !Presenter.IsSessionApproved
                && !Presenter.IsJoinPending);
        disconnectAllConnectionsCommand = new AsyncRelayCommand(
            _ => DisconnectAllConnectionsAsync(),
            _ => receiverRelayClient is not null && HasConnectedPresenter && HasReceiverSession);
        setReceiverAvailabilityCommand = new AsyncRelayCommand(
            async availability =>
            {
                if (availability is ReceiverAvailability requestedAvailability)
                {
                    await SetReceiverAvailabilityAsync(requestedAvailability);
                }
            },
            _ => receiverRelayClient is not null);
        ToggleSettingsCommand = new AsyncRelayCommand(async _ =>
        {
            IsAvailabilityMenuOpen = false;
            if (IsSettingsOpen)
            {
                await CloseSettingsAsync();
            }
            else
            {
                OpenSettings();
            }
        });
        CloseSettingsCommand = new AsyncRelayCommand(_ => CloseSettingsAsync());
        testServerConnectionCommand = new AsyncRelayCommand(
            _ => TestServerConnectionAsync(),
            _ => ShowTestServerConnectionButton
                && string.IsNullOrEmpty(ServerAddressValidationMessage)
                && !string.IsNullOrWhiteSpace(ServerAddress));
        ToggleAvailabilityMenuCommand = new AsyncRelayCommand(async _ =>
        {
            if (IsSettingsOpen)
            {
                await CloseSettingsAsync();
                if (IsSettingsOpen)
                {
                    return;
                }
            }

            IsAvailabilityMenuOpen = !IsAvailabilityMenuOpen;
        });
        ClearServerPasswordCommand = new AsyncRelayCommand(
            _ => ClearServerPasswordAsync(),
            _ => HasServerPassword);
        changeServerPasswordCommand = new RelayCommand(
            _ => BeginServerPasswordChange(),
            _ => HasServerPassword);
        applyServerPasswordCommand = new AsyncRelayCommand(
            _ => ApplyServerPasswordDraftAsync(),
            _ => ServerPasswordInput.Length > 0
                && string.IsNullOrEmpty(ServerPasswordValidationMessage));
        CancelServerPasswordChangeCommand = new RelayCommand(
            _ => CancelServerPasswordChange());
        incrementMaximumSendersCommand = new RelayCommand(
            _ => MaximumSenderConnections++,
            _ => MaximumSenderConnections < 16);
        decrementMaximumSendersCommand = new RelayCommand(
            _ => MaximumSenderConnections--,
            _ => MaximumSenderConnections > 1);

        RefreshMonitors();
    }

    public ObservableCollection<MonitorDescriptor> Monitors { get; } = [];

    public ObservableCollection<ConnectedPresenterDescriptor> ConnectedPresenters { get; } = [];

    public PresenterViewModel Presenter { get; }

    public string ApplicationVersion { get; } =
        global::ThisAssembly.AssemblyInformationalVersion.Split('+', 2)[0];

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

    public byte[]? PendingPresenterProfilePicturePng => pendingPresenter?.ProfilePicturePng;

    public bool HasPendingPresenter => pendingPresenter is not null;

    public bool HasConnectedPresenter
    {
        get => hasConnectedPresenter;
        private set
        {
            if (SetProperty(ref hasConnectedPresenter, value))
            {
                disconnectAllConnectionsCommand.RaiseCanExecuteChanged();
                Presenter.SetSenderRoleEnabled(!value);
                RaiseAvailabilityProperties();
                RaisePropertyChanged(nameof(FlyoutConnectionMessage));
            }
        }
    }

    public bool HasReceiverSession => receiverSessionId is not null;

    public bool CanSelectMonitor => Monitors.Count > 0;

    public IReadOnlyList<ReceiverAvailability> ReceiverAvailabilityOptions { get; } =
        [ReceiverAvailability.Available, ReceiverAvailability.Invisible];

    public ReceiverAvailability ReceiverAvailability
    {
        get => receiverAvailability;
        set
        {
            SetProperty(ref receiverAvailability, value);
            RaiseAvailabilityProperties();
        }
    }

    public string AvailabilityLabel => ReceiverAvailability == ReceiverAvailability.Invisible
        ? IsServerAvailable ? "Invisible" : "Server unavailable"
        : !IsServerAvailable
            ? "Server unavailable"
            : HasConnectedPresenter
                ? "Available and connected"
                : "Available";

    public string AvailabilityColor => !IsServerAvailable
        ? "#8B8B8B"
        : ReceiverAvailability == ReceiverAvailability.Invisible
            ? "#8B8B8B"
            : HasConnectedPresenter
                ? "#63C5DA"
                : "#6CCB7F";

    public bool IsServerAvailable =>
        receiverRelayClient?.Status is RelayConnectionStatus.Connected
            or RelayConnectionStatus.SessionExpired;

    public bool IsServerConfigurationMissing =>
        string.IsNullOrWhiteSpace(clientSettings?.Server.BaseUrl ?? receiverRelayClient?.ServerUrl);

    public string ServerConnectionGuidance => IsServerConfigurationMissing
        ? "Set the server address in Settings."
        : "Server not reachable. Check the server address in Settings.";

    public string EmptyClientListMessage => !IsServerAvailable
        ? ServerConnectionGuidance
        : ServerPasswordRequired && !HasServerPassword
            ? "Set a server password in Settings to see other clients."
            : "No available clients";

    public bool IsAvailabilityMenuOpen
    {
        get => isAvailabilityMenuOpen;
        set => SetProperty(ref isAvailabilityMenuOpen, value);
    }

    public bool IsSettingsOpen
    {
        get => isSettingsOpen;
        set => SetProperty(ref isSettingsOpen, value);
    }

    public string ServerAddress => string.IsNullOrWhiteSpace(ServerAddressInput)
        ? string.Empty
        : $"https://{ServerAddressInput.Trim()}";

    public string ServerAddressInput
    {
        get => serverAddressInput;
        set
        {
            var address = RemoveHttpsPrefix(value ?? string.Empty);
            if (SetProperty(ref serverAddressInput, address))
            {
                lastTestedServerAddress = null;
                lastServerConnectionTestSucceeded = false;
                IsServerAddressVerified = false;
                ServerConnectionTestMessage = string.Empty;
                RaisePropertyChanged(nameof(ServerAddress));
                RaisePropertyChanged(nameof(ServerAddressValidationMessage));
                RaiseServerAddressStateProperties();
            }
        }
    }

    public string ServerAddressValidationMessage
    {
        get
        {
            var input = ServerAddressInput.Trim();
            if (input.Length == 0
                && !string.IsNullOrWhiteSpace(clientSettings?.Server.BaseUrl))
            {
                return "Enter a server address.";
            }

            if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return "HTTPS is required. Remove http://; https:// is added automatically.";
            }

            if (input.Contains("://", StringComparison.Ordinal))
            {
                return "Enter the address without a protocol; https:// is added automatically.";
            }

            return ClientSettings.TryNormalizeServerAddress(
                ServerAddress,
                out _,
                out var validationMessage)
                ? string.Empty
                : validationMessage;
        }
    }

    public bool ShowTestServerConnectionButton => HasServerAddressChanged;

    public bool IsServerAddressVerified
    {
        get => isServerAddressVerified;
        private set => SetProperty(ref isServerAddressVerified, value);
    }

    public string ServerConnectionTestMessage
    {
        get => serverConnectionTestMessage;
        private set => SetProperty(ref serverConnectionTestMessage, value);
    }

    /// <summary>
    /// The draft from the password box. It is derived and discarded when settings are saved,
    /// and is never written to the preferences file or shown back to the user.
    /// </summary>
    public string ServerPasswordInput
    {
        get => serverPasswordInput;
        set
        {
            if (SetProperty(ref serverPasswordInput, value ?? string.Empty))
            {
                RaisePropertyChanged(nameof(ServerPasswordValidationMessage));
                applyServerPasswordCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasServerPassword
    {
        get => hasServerPassword;
        private set
        {
            if (SetProperty(ref hasServerPassword, value))
            {
                RaiseServerPasswordProperties();
            }
        }
    }

    public bool ServerPasswordRequired
    {
        get => serverPasswordRequired;
        private set
        {
            if (SetProperty(ref serverPasswordRequired, value))
            {
                RaiseServerPasswordProperties();
            }
        }
    }

    /// <summary>
    /// A stored password cannot be shown back, so the box is only offered when there is nothing to
    /// show: no password is set yet, or the user asked to replace the one that is.
    /// </summary>
    public bool IsChangingServerPassword
    {
        get => isChangingServerPassword;
        private set
        {
            if (SetProperty(ref isChangingServerPassword, value))
            {
                RaiseServerPasswordProperties();
            }
        }
    }

    public bool ShowServerPasswordEditor => !HasServerPassword || IsChangingServerPassword;

    public bool ShowServerPasswordSetState => HasServerPassword && !IsChangingServerPassword;

    public bool ShowServerPasswordWarning => !HasServerPassword;

    public string ServerPasswordWarning => ServerPasswordRequired
        ? "This relay requires a server password. Set one to see other clients and be seen by them."
        : "No server password is set. Your name and picture are visible to everyone who can reach this relay.";

    /// <summary>
    /// The password itself is never stored, so it cannot be shown back. This code can: it is
    /// the same on every client that uses the same password, which is what lets the user check
    /// two clients against each other without anyone typing a password in the open.
    /// </summary>
    public string ServerPasswordCheckCode =>
        ServerPasswordKey.DeriveCheckCode(clientSettings?.Server.PasswordKey) ?? string.Empty;

    public bool HasServerPasswordCheckCode => ServerPasswordCheckCode.Length > 0;

    public string ServerPasswordValidationMessage =>
        ServerPasswordInput.Length == 0
            || ServerPasswordKey.IsValidPassword(ServerPasswordInput)
            ? string.Empty
            : $"Use at least {ServerPasswordKey.MinimumPasswordLength} characters.";

    public string UserName
    {
        get => userName;
        set
        {
            if (SetProperty(ref userName, value))
            {
                RaisePropertyChanged(nameof(ProfileInitials));
            }
        }
    }

    public string ProfilePicturePath
    {
        get => profilePicturePath;
        set => SetProperty(ref profilePicturePath, value);
    }

    public int MaximumSenderConnections
    {
        get => maximumSenderConnections;
        set
        {
            if (SetProperty(ref maximumSenderConnections, Math.Clamp(value, 1, 16)))
            {
                incrementMaximumSendersCommand.RaiseCanExecuteChanged();
                decrementMaximumSendersCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLaunchAtStartup
    {
        get => isLaunchAtStartup;
        set => SetProperty(ref isLaunchAtStartup, value);
    }

    public bool ShowUsageHints
    {
        get => showUsageHints;
        set => SetProperty(ref showUsageHints, value);
    }

    public string ConnectedPresenterCountLabel =>
        $"{ConnectedPresenters.Count} sender{(ConnectedPresenters.Count == 1 ? string.Empty : "s")} connected";

    public string FlyoutConnectionMessage => HasConnectedPresenter
        ? ConnectedPresenterCountLabel
        : Presenter.ConnectionMessage;

    public string ProfileInitials
    {
        get
        {
            var parts = UserName.Split(
                ' ',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => "?",
                1 => FirstCharacter(parts[0]),
                _ => $"{FirstCharacter(parts[0])}{FirstCharacter(parts[^1])}",
            };
        }
    }

    /// <summary>
    /// Takes a whole text element rather than a single char, so a name starting with an emoji
    /// or any other character outside the basic plane is not split into half a surrogate pair.
    /// </summary>
    internal static string FirstCharacter(string value) =>
        (StringInfo.GetNextTextElement(value) is { Length: > 0 } element
            ? element
            : "?").ToUpperInvariant();

    public bool CanSetReceiverAvailability => receiverRelayClient is not null;

    public ICommand RefreshMonitorsCommand { get; }

    public ICommand ApprovePresenterCommand => approvePresenterCommand;

    public ICommand DisconnectAllConnectionsCommand => disconnectAllConnectionsCommand;

    public ICommand SetReceiverAvailabilityCommand => setReceiverAvailabilityCommand;

    public ICommand ToggleSettingsCommand { get; }

    public ICommand CloseSettingsCommand { get; }

    public ICommand TestServerConnectionCommand => testServerConnectionCommand;

    public ICommand ToggleAvailabilityMenuCommand { get; }

    public ICommand ClearServerPasswordCommand { get; }

    public ICommand ChangeServerPasswordCommand => changeServerPasswordCommand;

    public ICommand ApplyServerPasswordCommand => applyServerPasswordCommand;

    public ICommand CancelServerPasswordChangeCommand { get; }

    public ICommand IncrementMaximumSendersCommand => incrementMaximumSendersCommand;

    public ICommand DecrementMaximumSendersCommand => decrementMaximumSendersCommand;

    public async Task SetReceiverAvailabilityAsync(ReceiverAvailability availability)
    {
        var previousAvailability = ReceiverAvailability;
        if (previousAvailability != availability)
        {
            CancelAvailabilityRetry();
        }

        SetReceiverAvailabilitySilently(availability);
        SaveReceiverAvailabilityPreference(availability);
        await UpdateReceiverAvailabilityAsync(availability, previousAvailability);
    }

    public async Task InitializeAsync()
    {
        var presenterInitialization = Presenter.InitializeAsync();
        if (receiverRelayClient is not null)
        {
            try
            {
                var capabilities = await receiverRelayClient.GetRelayCapabilitiesAsync();
                ServerPasswordRequired = capabilities.ServerPasswordRequired;
            }
            catch (Exception exception)
            {
                SetStatus($"Relay capabilities could not be loaded: {exception.Message}", true);
            }
        }

        await presenterInitialization;
        if (ReceiverAvailability == ReceiverAvailability.Available && !HasReceiverSession)
        {
            await UpdateReceiverAvailabilityAsync(
                ReceiverAvailability.Available,
                ReceiverAvailability.Invisible);
        }
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
        var previousDisplayId = SelectedMonitor?.Display.DisplayId
            ?? clientSettings?.Receiver.SelectedDisplayId;

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
        RaisePropertyChanged(nameof(CanSelectMonitor));

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

    public async Task ApplySelectedMonitorAsync()
    {
        if (receiverSessionId is null || receiverRelayClient is null || SelectedMonitor is null)
        {
            return;
        }

        try
        {
            overlayService.Show(SelectedMonitor);
            await receiverRelayClient.UpdateReceiverDisplayAsync(SelectedMonitor.Display);
            SetStatus("Receiving screen updated.", false);
        }
        catch (Exception exception)
        {
            SetStatus($"The receiving screen could not be updated: {exception.Message}", true);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancelAvailabilityRetry();
        overlayService.StateChanged -= OnOverlayStateChanged;
        if (receiverRelayClient is not null)
        {
            receiverRelayClient.ConnectionStatusChanged -= OnReceiverConnectionStatusChanged;
            receiverRelayClient.PresenterJoinRequested -= OnPresenterJoinRequested;
            receiverRelayClient.PresenterJoinCancelled -= OnPresenterJoinCancelled;
            receiverRelayClient.SessionApproved -= OnReceiverSessionApproved;
            receiverRelayClient.PointerReceived -= OnPointerReceived;
            receiverRelayClient.SessionEnded -= OnReceiverSessionEnded;
            RelayClientShutdown.Complete(receiverRelayClient);
        }

        overlayService.Dispose();
        targetRegionService.UsageHintsShown -= OnUsageHintsShown;
        Presenter.PropertyChanged -= OnPresenterPropertyChanged;
        Presenter.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task CreateReceiverSessionAsync()
    {
        if (SelectedMonitor is null || receiverRelayClient is null)
        {
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
            ClearReceiverSession(preserveAvailability: true);
            SetStatus($"The receiver could not become available: {exception.Message}", true);
        }
    }

    public async Task ApprovePendingPresenterAsync()
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

    public async Task RejectPendingPresenterAsync()
    {
        if (receiverRelayClient is null || receiverSessionId is null || pendingPresenter is null)
        {
            return;
        }

        try
        {
            await receiverRelayClient.RejectPresenterAsync(
                receiverSessionId,
                pendingPresenter.ConnectionId);
            SetStatus($"Declined {pendingPresenter.DisplayName}.", false);
            SetPendingPresenter(null);
        }
        catch (Exception exception)
        {
            SetStatus($"The presenter request could not be declined: {exception.Message}", true);
            throw;
        }
    }

    private async Task DisconnectAllConnectionsAsync()
    {
        if (receiverRelayClient is null || !HasConnectedPresenter)
        {
            return;
        }

        try
        {
            await receiverRelayClient.DisconnectAllConnectionsAsync();
            SetPendingPresenter(null);
            SetStatus("Disconnected all presenter connections.", false);
        }
        catch (Exception exception)
        {
            SetStatus($"Presenter connections could not be disconnected: {exception.Message}", true);
        }
    }

    private async Task UpdateReceiverAvailabilityAsync(
        ReceiverAvailability requestedAvailability,
        ReceiverAvailability previousAvailability)
    {
        await availabilityUpdateGate.WaitAsync();
        try
        {
            await UpdateReceiverAvailabilityCoreAsync(
                requestedAvailability,
                previousAvailability);
        }
        finally
        {
            availabilityUpdateGate.Release();
        }
    }

    private async Task UpdateReceiverAvailabilityCoreAsync(
        ReceiverAvailability requestedAvailability,
        ReceiverAvailability previousAvailability)
    {
        IsAvailabilityMenuOpen = false;
        if (receiverRelayClient is null)
        {
            SetReceiverAvailabilitySilently(previousAvailability);
            return;
        }

        try
        {
            if (requestedAvailability == ReceiverAvailability.Invisible
                && !HasReceiverSession)
            {
                SetReceiverAvailabilitySilently(ReceiverAvailability.Invisible);
                CancelAvailabilityRetry();
                SetStatus("This receiver is invisible to presenters.", false);
                return;
            }

            if (requestedAvailability == ReceiverAvailability.Available
                && !HasReceiverSession)
            {
                await CreateReceiverSessionAsync();
                if (!HasReceiverSession)
                {
                    SetReceiverAvailabilitySilently(ReceiverAvailability.Available);
                    ScheduleAvailabilityRetry();
                }
                else
                {
                    CancelAvailabilityRetry();
                }
                return;
            }

            var isAvailable = await receiverRelayClient.SetReceiverDiscoverableAsync(
                requestedAvailability == ReceiverAvailability.Available);
            SetReceiverAvailabilitySilently(
                isAvailable ? ReceiverAvailability.Available : ReceiverAvailability.Invisible);
            CancelAvailabilityRetry();
            SetStatus(
                isAvailable
                    ? "This receiver is available for access requests."
                    : "This receiver is invisible to presenters.",
                false);
        }
        catch (Exception exception)
        {
            SetReceiverAvailabilitySilently(requestedAvailability);
            ScheduleAvailabilityRetry();
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
        RaisePropertyChanged(nameof(IsServerAvailable));
        RaisePropertyChanged(nameof(ServerConnectionGuidance));
        RaisePropertyChanged(nameof(EmptyClientListMessage));
        RaiseAvailabilityProperties();
    }

    private void OnPresenterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is nameof(PresenterViewModel.IsSessionApproved)
            or nameof(PresenterViewModel.IsJoinPending))
        {
            approvePresenterCommand.RaiseCanExecuteChanged();
        }

        if (e.PropertyName == nameof(PresenterViewModel.ConnectionMessage))
        {
            RaisePropertyChanged(nameof(FlyoutConnectionMessage));
        }
    }

    private void OnPresenterJoinRequested(object? sender, PresenterJoinRequestedEventArgs e)
    {
        SetPendingPresenter(e.Presenter);
        SetStatus($"{e.Presenter.DisplayName} is waiting for approval.", false);
    }

    private void OnReceiverSessionApproved(object? sender, RelaySessionStateEventArgs e)
    {
        var presenterDisconnected = HasConnectedPresenter && !e.State.Approved;
        ConnectedPresenters.Clear();
        foreach (var presenter in e.State.ConnectedPresenters ?? [])
        {
            ConnectedPresenters.Add(presenter);
        }

        if (e.State.Approved && ConnectedPresenters.Count == 0)
        {
            ConnectedPresenters.Add(new ConnectedPresenterDescriptor("Connected sender"));
        }

        RaisePropertyChanged(nameof(ConnectedPresenterCountLabel));
        RaisePropertyChanged(nameof(FlyoutConnectionMessage));
        HasConnectedPresenter = ConnectedPresenters.Count > 0;
        if (receiverRelayClient?.Credential?.Role == ClientRole.Receiver)
        {
            receiverSessionId = e.State.SessionId;
            SetReceiverAvailabilitySilently(
                e.State.ReceiverDiscoverable
                    ? ReceiverAvailability.Available
                    : ReceiverAvailability.Invisible);
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
            HasConnectedPresenter
                ? $"Receiving pointers from {ConnectedPresenters.Count} connected sender{(ConnectedPresenters.Count == 1 ? string.Empty : "s")}."
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
        var retryAvailability = ReceiverAvailability == ReceiverAvailability.Available;
        overlayService.Hide();
        ClearReceiverSession(preserveAvailability: retryAvailability);
        if (retryAvailability)
        {
            ScheduleAvailabilityRetry();
        }
        SetStatus(e.Reason, e.Expired);
    }

    private void SetPendingPresenter(PresenterDescriptor? presenter)
    {
        pendingPresenter = presenter;
        RaisePropertyChanged(nameof(PendingPresenterName));
        RaisePropertyChanged(nameof(PendingPresenterProfilePicturePng));
        RaisePropertyChanged(nameof(HasPendingPresenter));
        approvePresenterCommand.RaiseCanExecuteChanged();
    }

    internal async Task TestServerConnectionAsync()
    {
        if (clientSettings is null)
        {
            SetStatus("Settings are not available in this client configuration.", true);
            return;
        }

        if (!TryGetRequestedServerAddress(out var requestedServerAddress))
        {
            ServerConnectionTestMessage = string.IsNullOrEmpty(ServerAddressValidationMessage)
                ? "Enter a server address to test the connection."
                : ServerAddressValidationMessage;
            return;
        }

        ServerConnectionTestMessage = "Testing connection...";
        var result = await serverConnectionTester.TestAsync(requestedServerAddress);
        lastTestedServerAddress = requestedServerAddress;
        lastServerConnectionTestSucceeded = result.IsSuccessful;
        if (!result.IsSuccessful)
        {
            IsServerAddressVerified = false;
            ServerConnectionTestMessage = result.Message;
            return;
        }

        try
        {
            if (!TryApproveServerAddressChange(requestedServerAddress))
            {
                ResetServerAddressDraft();
                return;
            }

            PersistSettings(requestedServerAddress);
            pendingRelayReinitialization = !string.Equals(
                activeServerAddress,
                requestedServerAddress,
                StringComparison.Ordinal);
            ServerConnectionTestMessage = string.Empty;
            IsServerAddressVerified = true;
            RaiseServerAddressStateProperties();
            SetStatus("Server connection verified and address saved.", false);
        }
        catch (Exception exception)
        {
            SetStatus($"Settings could not be saved: {exception.Message}", true);
            ServerConnectionTestMessage = $"The address could not be saved: {exception.Message}";
        }
    }

    private void OnPresenterJoinCancelled(object? sender, PresenterJoinCancelledEventArgs e)
    {
        if (!string.Equals(
                pendingPresenter?.ConnectionId,
                e.PresenterConnectionId,
                StringComparison.Ordinal))
        {
            return;
        }

        SetPendingPresenter(null);
        SetStatus("The sender withdrew its connection request.", false);
    }

    internal async Task CloseSettingsAsync()
    {
        if (clientSettings is null)
        {
            IsSettingsOpen = false;
            return;
        }

        try
        {
            await ApplyServerPasswordDraftAsync();
            if (HasServerAddressChanged)
            {
                if (!TryGetRequestedServerAddress(out var requestedServerAddress))
                {
                    ResetServerAddressDraft();
                }
                else
                {
                    var currentAddressWasTested = string.Equals(
                        lastTestedServerAddress,
                        requestedServerAddress,
                        StringComparison.Ordinal);
                    if (!currentAddressWasTested)
                    {
                        await TestServerConnectionAsync();
                    }

                    if (HasServerAddressChanged
                        && (!string.Equals(
                                lastTestedServerAddress,
                                requestedServerAddress,
                                StringComparison.Ordinal)
                            || !lastServerConnectionTestSucceeded))
                    {
                        ResetServerAddressDraft();
                    }
                }
            }

            PersistSettings(clientSettings.Server.BaseUrl);
            IsSettingsOpen = false;
            SetStatus("Settings saved.", false);
        }
        catch (Exception exception)
        {
            SetStatus($"Settings could not be saved: {exception.Message}", true);
            return;
        }

        if (pendingRelayReinitialization)
        {
            RelayReinitializationRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            await ApplyActiveClientSettingsAsync();
        }
        catch (Exception exception)
        {
            SetStatus(
                $"Settings were saved, but the active connection could not be updated: {exception.Message}",
                true);
        }
    }

    private void OpenSettings()
    {
        ServerPasswordInput = string.Empty;
        IsChangingServerPassword = false;
        ResetSettingsDraft();
        IsServerAddressVerified = false;
        ServerConnectionTestMessage = string.Empty;
        IsSettingsOpen = true;
    }

    private bool HasServerAddressChanged
    {
        get
        {
            if (!ClientSettings.TryNormalizeServerAddress(
                    ServerAddress,
                    out var requestedServerAddress,
                    out _))
            {
                return true;
            }

            return !string.Equals(
                clientSettings?.Server.BaseUrl ?? activeServerAddress,
                requestedServerAddress,
                StringComparison.Ordinal);
        }
    }

    private bool TryGetRequestedServerAddress(out string requestedServerAddress)
    {
        if (!string.IsNullOrEmpty(ServerAddressValidationMessage)
            || !ClientSettings.TryNormalizeServerAddress(
                ServerAddress,
                out requestedServerAddress,
                out _)
            || string.IsNullOrWhiteSpace(requestedServerAddress))
        {
            requestedServerAddress = string.Empty;
            return false;
        }

        return true;
    }

    private bool TryApproveServerAddressChange(string requestedServerAddress)
    {
        var savedServerAddress = clientSettings?.Server.BaseUrl ?? string.Empty;
        if (string.Equals(savedServerAddress, requestedServerAddress, StringComparison.Ordinal))
        {
            return true;
        }

        var hasActiveConnections = HasConnectedPresenter
            || HasPendingPresenter
            || Presenter.IsSessionApproved
            || Presenter.IsJoinPending;
        if (!hasActiveConnections)
        {
            return true;
        }

        var changeRequest = new ServerAddressChangeRequestedEventArgs(
            savedServerAddress,
            requestedServerAddress);
        ServerAddressChangeRequested?.Invoke(this, changeRequest);
        return changeRequest.Approved;
    }

    private void PersistSettings(string serverAddress)
    {
        clientSettings!.SaveUserPreferences(
            serverAddress,
            UserName,
            ProfilePicturePath,
            MaximumSenderConnections,
            IsLaunchAtStartup,
            SelectedMonitor?.Display.DisplayId,
            ShowUsageHints,
            ReceiverAvailability == ReceiverAvailability.Available);
        Presenter.SetUsageHintsState(ShowUsageHints, hasShownUsageHints);
        startupRegistrationService?.SetEnabled(IsLaunchAtStartup);
        RaiseServerAddressStateProperties();
    }

    private Task ApplyActiveClientSettingsAsync() => Task.WhenAll(
        receiverRelayClient?.ApplyClientSettingsAsync(
            UserName,
            ProfilePicturePath,
            MaximumSenderConnections) ?? Task.CompletedTask,
        Presenter.ApplyClientSettingsAsync(
            UserName,
            ProfilePicturePath,
            MaximumSenderConnections));

    private void ResetServerAddressDraft()
    {
        ServerAddressInput = RemoveHttpsPrefix(clientSettings?.Server.BaseUrl ?? string.Empty);
        ServerConnectionTestMessage = string.Empty;
    }

    private void RaiseServerAddressStateProperties()
    {
        RaisePropertyChanged(nameof(ShowTestServerConnectionButton));
        testServerConnectionCommand.RaiseCanExecuteChanged();
    }

    private void ResetSettingsDraft()
    {
        if (clientSettings is null)
        {
            return;
        }

        ServerAddressInput = RemoveHttpsPrefix(clientSettings.Server.BaseUrl);
        UserName = clientSettings.Profile.UserName;
        ProfilePicturePath = clientSettings.Profile.PicturePath;
        MaximumSenderConnections = clientSettings.Receiver.MaximumSenderConnections;
        IsLaunchAtStartup = startupRegistrationService?.IsEnabled
            ?? clientSettings.Startup.LaunchAtStartup;
        ShowUsageHints = clientSettings.Pointer.ShowUsageHints;

        var savedMonitor = Monitors.FirstOrDefault(
            monitor => string.Equals(
                monitor.Display.DisplayId,
                clientSettings.Receiver.SelectedDisplayId,
                StringComparison.OrdinalIgnoreCase));
        if (savedMonitor is not null)
        {
            SelectedMonitor = savedMonitor;
        }
    }

    private static string RemoveHttpsPrefix(string address) =>
        address.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? address.TrimStart()["https://".Length..]
            : address;

    private void ClearReceiverSession(bool preserveAvailability = false)
    {
        receiverSessionId = null;
        ConnectedPresenters.Clear();
        RaisePropertyChanged(nameof(ConnectedPresenterCountLabel));
        RaisePropertyChanged(nameof(FlyoutConnectionMessage));
        HasConnectedPresenter = false;
        if (!preserveAvailability)
        {
            SetReceiverAvailabilitySilently(ReceiverAvailability.Invisible);
        }
        SetPendingPresenter(null);
        RaiseReceiverSessionProperties();
    }

    private void SetReceiverAvailabilitySilently(ReceiverAvailability availability)
    {
        ReceiverAvailability = availability;
    }

    private void SaveReceiverAvailabilityPreference(ReceiverAvailability availability)
    {
        if (clientSettings is null)
        {
            return;
        }

        try
        {
            clientSettings.SaveReceiverAvailability(
                availability == ReceiverAvailability.Available);
        }
        catch (Exception exception)
        {
            SetStatus($"Receiver availability preference could not be saved: {exception.Message}", true);
        }
    }

    private void OnUsageHintsShown(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (hasShownUsageHints)
        {
            return;
        }

        hasShownUsageHints = true;
        try
        {
            clientSettings?.SaveUsageHintsShown();
        }
        catch (Exception exception)
        {
            SetStatus($"Usage hint state could not be saved: {exception.Message}", true);
        }
    }

    private void ScheduleAvailabilityRetry()
    {
        var selectedAvailability = ReceiverAvailability;
        var updateNeeded = selectedAvailability == ReceiverAvailability.Available
            ? !HasReceiverSession
            : HasReceiverSession;
        if (disposed
            || !updateNeeded
            || availabilityRetryCancellation is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        availabilityRetryCancellation = cancellation;
        _ = RetryAvailabilityAsync(cancellation, selectedAvailability);
    }

    private async Task RetryAvailabilityAsync(
        CancellationTokenSource cancellation,
        ReceiverAvailability availability)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested
                   && ReceiverAvailability == availability)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                if (cancellationToken.IsCancellationRequested
                    || ReceiverAvailability != availability)
                {
                    return;
                }

                await UpdateReceiverAvailabilityAsync(
                    availability,
                    availability);
            }
        }
        catch (OperationCanceledException)
        {
            // The user selected Invisible or the view model was disposed.
        }
        finally
        {
            if (ReferenceEquals(availabilityRetryCancellation, cancellation))
            {
                availabilityRetryCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelAvailabilityRetry()
    {
        var cancellation = availabilityRetryCancellation;
        availabilityRetryCancellation = null;
        cancellation?.Cancel();
    }

    /// <summary>
    /// Derives the group key from the draft password, stores it protected, and hands it to both
    /// relay connections. The password is used here and nowhere else.
    /// </summary>
    internal async Task ApplyServerPasswordDraftAsync()
    {
        var draft = ServerPasswordInput;
        if (draft.Length == 0)
        {
            return;
        }

        if (!ServerPasswordKey.IsValidPassword(draft))
        {
            SetStatus(ServerPasswordValidationMessage, true);
            return;
        }

        var key = await Task.Run(() => ServerPasswordKey.Derive(draft));
        ServerPasswordInput = string.Empty;
        serverPasswordStore?.Save(key);
        HasServerPassword = true;
        IsChangingServerPassword = false;
        await ApplyServerPasswordKeyAsync(key);
    }

    private void BeginServerPasswordChange()
    {
        ServerPasswordInput = string.Empty;
        IsChangingServerPassword = true;
    }

    private void CancelServerPasswordChange()
    {
        ServerPasswordInput = string.Empty;
        IsChangingServerPassword = false;
    }

    public async Task ClearServerPasswordAsync()
    {
        ServerPasswordInput = string.Empty;
        IsChangingServerPassword = false;
        serverPasswordStore?.Clear();
        HasServerPassword = false;
        await ApplyServerPasswordKeyAsync(null);
    }

    /// <summary>
    /// Both roles hold their own relay connection and the relay groups each one separately, so
    /// a password change has to reach both before this client stops being visible and reachable
    /// under the previous one.
    /// </summary>
    private async Task ApplyServerPasswordKeyAsync(string? key)
    {
        if (clientSettings is not null)
        {
            clientSettings.Server.PasswordKey = key;
        }

        // Raised here rather than from HasServerPassword, which does not change when one
        // password replaces another — the case where the code is worth reading.
        RaisePropertyChanged(nameof(ServerPasswordCheckCode));
        RaisePropertyChanged(nameof(HasServerPasswordCheckCode));
        try
        {
            if (receiverRelayClient is not null)
            {
                await receiverRelayClient.SetServerPasswordKeyAsync(key);
            }

            await Presenter.SetServerPasswordKeyAsync(key);
        }
        catch (Exception exception)
        {
            SetStatus(
                $"The server password was saved, but the relay could not be updated: {exception.Message}",
                true);
        }
    }

    private void RaiseServerPasswordProperties()
    {
        RaisePropertyChanged(nameof(ShowServerPasswordEditor));
        RaisePropertyChanged(nameof(ShowServerPasswordSetState));
        RaisePropertyChanged(nameof(ShowServerPasswordWarning));
        RaisePropertyChanged(nameof(ServerPasswordWarning));
        RaisePropertyChanged(nameof(ServerPasswordCheckCode));
        RaisePropertyChanged(nameof(HasServerPasswordCheckCode));
        RaisePropertyChanged(nameof(EmptyClientListMessage));
        changeServerPasswordCommand.RaiseCanExecuteChanged();
        (ClearServerPasswordCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaiseAvailabilityProperties()
    {
        RaisePropertyChanged(nameof(AvailabilityLabel));
        RaisePropertyChanged(nameof(AvailabilityColor));
    }

    private void RaiseReceiverSessionProperties()
    {
        RaisePropertyChanged(nameof(HasReceiverSession));
        RaisePropertyChanged(nameof(CanSelectMonitor));
        RaisePropertyChanged(nameof(CanSetReceiverAvailability));
        approvePresenterCommand.RaiseCanExecuteChanged();
        disconnectAllConnectionsCommand.RaiseCanExecuteChanged();
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

public sealed class ServerAddressChangeRequestedEventArgs(
    string currentServerAddress,
    string requestedServerAddress) : EventArgs
{
    public string CurrentServerAddress { get; } = currentServerAddress;

    public string RequestedServerAddress { get; } = requestedServerAddress;

    public bool Approved { get; set; }
}
