using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using RemotePointer.Client.Configuration;
using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Validation;

namespace RemotePointer.Client.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// How long after the last pointer event an annotator still counts as annotating. A gesture
    /// pauses between strokes, so a short window would flicker the indicator off mid-drawing.
    /// </summary>
    private const long AnnotatingIdleMilliseconds = 1_500;

    private const int AnnotatingPollMilliseconds = 300;

    private readonly AsyncRelayCommand applyServerPasswordCommand;
    private readonly AsyncRelayCommand approveAnnotatorCommand;
    private readonly SemaphoreSlim availabilityUpdateGate = new(1, 1);
    private readonly RelayCommand changeServerPasswordCommand;
    private readonly AsyncRelayCommand disconnectAllConnectionsCommand;
    private readonly AsyncRelayCommand togglePauseAllCommand;
    private readonly Dictionary<string, long> lastPointerReceivedAt = new(StringComparer.Ordinal);
    private readonly DispatcherTimer annotatingTimer;
    private readonly AsyncRelayCommand setHostAvailabilityCommand;
    private readonly AsyncRelayCommand testServerConnectionCommand;
    private readonly IMonitorService monitorService;
    private readonly IHostOverlayService overlayService;
    private readonly IRelayClient? hostRelayClient;
    private readonly ClientSettings? clientSettings;
    private readonly IStartupRegistrationService? startupRegistrationService;
    private readonly IServerConnectionTester serverConnectionTester;
    private readonly IServerPasswordStore? serverPasswordStore;
    private readonly ITargetRegionService targetRegionService;
    private readonly RelayCommand decrementMaximumAnnotatorsCommand;
    private readonly RelayCommand incrementMaximumAnnotatorsCommand;
    private CancellationTokenSource? availabilityRetryCancellation;
    private bool disposed;
    private bool isError;
    private bool isOverlayVisible;
    private bool hasConnectedAnnotator;
    private HostAvailability hostAvailability;
    private AnnotatorDescriptor? pendingAnnotator;
    private string hostConnectionMessage;
    private MonitorDescriptor? selectedMonitor;
    private string? hostSessionId;
    private string statusMessage = "Select a monitor to begin.";
    private bool isAvailabilityMenuOpen;
    private bool isSettingsOpen;
    private string profilePicturePath;
    private string serverAddressInput;
    private string userName;
    private int maximumAnnotatorConnections;
    private bool isLaunchAtStartup;
    private bool showUsageHints = true;
    private bool hasShownUsageHints;
    private int drawingOpacityPercent = PointerSettings.DefaultDrawingOpacityPercent;
    private string annotationColor = AnnotationColors.Default;
    private bool isServerAddressVerified;
    private bool hasServerPassword;
    private bool isChangingServerPassword;
    private bool serverPasswordRequired;
    private string serverPasswordInput = string.Empty;
    private bool pendingRelayReinitialization;
    private string? lastTestedServerAddress;
    private bool lastServerConnectionTestSucceeded;
    private string serverConnectionTestMessage = string.Empty;
    private string serverVersionLabel = string.Empty;
    private readonly string activeServerAddress;

    public event EventHandler<ServerAddressChangeRequestedEventArgs>? ServerAddressChangeRequested;

    public event EventHandler? RelayReinitializationRequested;

    public MainWindowViewModel(
        IMonitorService monitorService,
        IHostOverlayService overlayService,
        ITargetRegionService? targetRegionService = null,
        IRelayClient? hostRelayClient = null,
        IRelayClient? annotatorRelayClient = null,
        int pointerTtlMilliseconds = 2_000,
        ClientSettings? clientSettings = null,
        IStartupRegistrationService? startupRegistrationService = null,
        IServerConnectionTester? serverConnectionTester = null,
        IServerPasswordStore? serverPasswordStore = null)
    {
        this.monitorService = monitorService ?? throw new ArgumentNullException(nameof(monitorService));
        this.overlayService = overlayService ?? throw new ArgumentNullException(nameof(overlayService));
        this.hostRelayClient = hostRelayClient;
        this.clientSettings = clientSettings;
        this.startupRegistrationService = startupRegistrationService;
        this.serverConnectionTester = serverConnectionTester ?? new ServerConnectionTester();
        this.serverPasswordStore = serverPasswordStore;
        hasServerPassword = !string.IsNullOrWhiteSpace(clientSettings?.Server.PasswordKey);
        var configuredServerAddress = clientSettings?.Server.BaseUrl
            ?? hostRelayClient?.ServerUrl
            ?? string.Empty;
        activeServerAddress = configuredServerAddress;
        serverAddressInput = RemoveHttpsPrefix(configuredServerAddress);
        userName = clientSettings?.Profile.UserName ?? Environment.UserName;
        profilePicturePath = clientSettings?.Profile.PicturePath ?? string.Empty;
        maximumAnnotatorConnections = clientSettings?.Host.MaximumAnnotatorConnections ?? 2;
        hostAvailability = clientSettings?.Host.IsAvailable == true
            ? HostAvailability.Available
            : HostAvailability.Invisible;
        isLaunchAtStartup = startupRegistrationService?.IsEnabled
            ?? clientSettings?.Startup.LaunchAtStartup
            ?? false;
        showUsageHints = clientSettings?.Pointer.ShowUsageHints ?? true;
        hasShownUsageHints = clientSettings?.Pointer.HasShownUsageHints ?? false;
        drawingOpacityPercent = PointerSettings.ClampDrawingOpacityPercent(
            clientSettings?.Pointer.DrawingOpacityPercent
            ?? PointerSettings.DefaultDrawingOpacityPercent);
        annotationColor = AnnotationColors.Normalize(clientSettings?.Pointer.AnnotationColor);
        AnnotationColorOptions = [.. AnnotationColorPresets.Select(
            preset => new AnnotationColorOption(preset.Name, preset.Color))];
        RefreshAnnotationColorSelection();
        SelectAnnotationColorCommand = new RelayCommand(
            option =>
            {
                if (option is AnnotationColorOption selected)
                {
                    AnnotationColor = selected.Color;
                }
            });
        this.overlayService.StateChanged += OnOverlayStateChanged;
        this.targetRegionService = targetRegionService ?? new TargetRegionService();
        this.targetRegionService.UsageHintsShown += OnUsageHintsShown;
        Annotator = new AnnotatorViewModel(
            this.targetRegionService,
            annotatorRelayClient,
            pointerTtlMilliseconds);
        Annotator.SetUsageHintsState(showUsageHints, hasShownUsageHints);
        Annotator.SetDrawingOpacityPercent(drawingOpacityPercent);
        Annotator.SetAnnotationColor(annotationColor);
        Annotator.PropertyChanged += OnAnnotatorPropertyChanged;

        hostConnectionMessage = hostRelayClient is null
            ? "Networking is not configured."
            : "Disconnected.";

        if (hostRelayClient is not null)
        {
            hostRelayClient.ConnectionStatusChanged += OnHostConnectionStatusChanged;
            hostRelayClient.AnnotatorJoinRequested += OnAnnotatorJoinRequested;
            hostRelayClient.AnnotatorJoinCancelled += OnAnnotatorJoinCancelled;
            hostRelayClient.SessionApproved += OnHostSessionApproved;
            hostRelayClient.PointerReceived += OnPointerReceived;
            hostRelayClient.SessionEnded += OnHostSessionEnded;
        }

        RefreshMonitorsCommand = new RelayCommand(_ => RefreshMonitors());
        approveAnnotatorCommand = new AsyncRelayCommand(
            _ => ApprovePendingAnnotatorAsync(),
            _ => hostRelayClient is not null
                && pendingAnnotator is not null
                && HasHostSession
                && !Annotator.IsSessionApproved
                && !Annotator.IsJoinPending);
        disconnectAllConnectionsCommand = new AsyncRelayCommand(
            _ => DisconnectAllConnectionsAsync(),
            _ => hostRelayClient is not null && HasConnectedAnnotator && HasHostSession);
        togglePauseAllCommand = new AsyncRelayCommand(
            _ => SetAllAnnotatorsPausedAsync(!AreAllAnnotatorsPaused),
            _ => hostRelayClient is not null && HasConnectedAnnotator && HasHostSession);
        // Annotating is inferred from the pointer stream rather than announced, so it needs a
        // clock to decide when a stream that simply stopped has gone quiet.
        annotatingTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(AnnotatingPollMilliseconds),
        };
        annotatingTimer.Tick += OnAnnotatingTimerTick;
        setHostAvailabilityCommand = new AsyncRelayCommand(
            async availability =>
            {
                if (availability is HostAvailability requestedAvailability)
                {
                    await SetHostAvailabilityAsync(requestedAvailability);
                }
            },
            _ => hostRelayClient is not null);
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
        // Testing an address that is already saved is allowed on purpose: it is the only way to
        // confirm a relay is still reachable without editing the address first.
        testServerConnectionCommand = new AsyncRelayCommand(
            _ => TestServerConnectionAsync(),
            _ => string.IsNullOrEmpty(ServerAddressValidationMessage)
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
        incrementMaximumAnnotatorsCommand = new RelayCommand(
            _ => MaximumAnnotatorConnections++,
            _ => MaximumAnnotatorConnections < 16);
        decrementMaximumAnnotatorsCommand = new RelayCommand(
            _ => MaximumAnnotatorConnections--,
            _ => MaximumAnnotatorConnections > 1);

        RefreshMonitors();
    }

    public ObservableCollection<MonitorDescriptor> Monitors { get; } = [];

    public ObservableCollection<ConnectedAnnotatorViewModel> ConnectedAnnotators { get; } = [];

    public AnnotatorViewModel Annotator { get; }

    public string ApplicationVersion { get; } =
        global::ThisAssembly.AssemblyInformationalVersion.Split('+', 2)[0];

    public MonitorDescriptor? SelectedMonitor
    {
        get => selectedMonitor;
        set
        {
            if (SetProperty(ref selectedMonitor, value))
            {
                RaisePropertyChanged(nameof(CanSetHostAvailability));
                setHostAvailabilityCommand.RaiseCanExecuteChanged();
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

    public string HostConnectionMessage
    {
        get => hostConnectionMessage;
        private set => SetProperty(ref hostConnectionMessage, value);
    }

    // "Relay" rather than "Server" in the name: this is the relay address the Host role's
    // connection uses, not an address belonging to the Host.
    public string HostRelayUrl => hostRelayClient?.ServerUrl ?? "Not configured";

    public string PendingAnnotatorName => pendingAnnotator?.DisplayName ?? "No annotator waiting for approval.";

    public byte[]? PendingAnnotatorProfilePicturePng => pendingAnnotator?.ProfilePicturePng;

    public bool HasPendingAnnotator => pendingAnnotator is not null;

    public bool HasConnectedAnnotator
    {
        get => hasConnectedAnnotator;
        private set
        {
            if (SetProperty(ref hasConnectedAnnotator, value))
            {
                disconnectAllConnectionsCommand.RaiseCanExecuteChanged();
                Annotator.SetRoleEnabled(!value);
                RaiseAvailabilityProperties();
                RaisePropertyChanged(nameof(FlyoutConnectionMessage));
            }
        }
    }

    public bool HasHostSession => hostSessionId is not null;

    public bool CanSelectMonitor => Monitors.Count > 0;

    public IReadOnlyList<HostAvailability> HostAvailabilityOptions { get; } =
        [HostAvailability.Available, HostAvailability.Invisible];

    public HostAvailability HostAvailability
    {
        get => hostAvailability;
        set
        {
            SetProperty(ref hostAvailability, value);
            RaiseAvailabilityProperties();
        }
    }

    // The profile bar reports availability only. Being in a session is shown per client — on the
    // annotator's host row and on the host's annotator rows — so it is not repeated here.
    public string AvailabilityLabel => !IsServerAvailable
        ? "Server unavailable"
        : HostAvailability == HostAvailability.Invisible
            ? "Invisible"
            : "Available";

    public string AvailabilityColor => !IsServerAvailable
        ? "#8B8B8B"
        : HostAvailability == HostAvailability.Invisible
            ? "#8B8B8B"
            : "#6CCB7F";

    public bool IsServerAvailable =>
        hostRelayClient?.Status is RelayConnectionStatus.Connected
            or RelayConnectionStatus.SessionExpired;

    public bool IsServerConfigurationMissing =>
        string.IsNullOrWhiteSpace(clientSettings?.Server.BaseUrl ?? hostRelayClient?.ServerUrl);

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
                ServerVersionLabel = string.Empty;
                RaisePropertyChanged(nameof(ServerAddress));
                RaisePropertyChanged(nameof(ServerAddressValidationMessage));
                RaiseServerAddressCommandState();
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
    /// The version the last reached server advertised, empty when it is unknown. It is refreshed
    /// by every connection test and cleared as soon as the address is edited. It is rendered next
    /// to the verified checkmark, which carries the success message on its own.
    /// </summary>
    public string ServerVersionLabel
    {
        get => serverVersionLabel;
        private set
        {
            if (SetProperty(ref serverVersionLabel, value))
            {
                RaisePropertyChanged(nameof(HasServerVersion));
            }
        }
    }

    public bool HasServerVersion => ServerVersionLabel.Length > 0;

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

    public int MaximumAnnotatorConnections
    {
        get => maximumAnnotatorConnections;
        set
        {
            if (SetProperty(ref maximumAnnotatorConnections, Math.Clamp(value, 1, 16)))
            {
                incrementMaximumAnnotatorsCommand.RaiseCanExecuteChanged();
                decrementMaximumAnnotatorsCommand.RaiseCanExecuteChanged();
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

    public int DrawingOpacityPercent
    {
        get => drawingOpacityPercent;
        set
        {
            if (SetProperty(
                    ref drawingOpacityPercent,
                    PointerSettings.ClampDrawingOpacityPercent(value)))
            {
                RaisePropertyChanged(nameof(DrawingOpacityLabel));
            }
        }
    }

    public string DrawingOpacityLabel =>
        DrawingOpacityPercent.ToString(CultureInfo.CurrentCulture) + "%";

    public int MinimumDrawingOpacityPercent => PointerSettings.MinimumDrawingOpacityPercent;

    public int MaximumDrawingOpacityPercent => PointerSettings.MaximumDrawingOpacityPercent;

    /// <summary>
    /// Names for the swatches, in the order of <see cref="AnnotationColors.Palette"/>. The
    /// colours themselves live in the contract because the relay allocates from the same list
    /// when it has to move an annotator off a colour somebody else already holds — a preset the
    /// settings pane could not name would arrive as an unexplained colour.
    /// </summary>
    internal static readonly string[] AnnotationColorNames =
        ["Red", "Amber", "Green", "Cyan", "Blue", "Violet", "Pink"];

    internal static IEnumerable<(string Name, string Color)> AnnotationColorPresets =>
        AnnotationColorNames.Zip(
            AnnotationColors.Palette,
            (name, color) => (Name: name, Color: color));

    public IReadOnlyList<AnnotationColorOption> AnnotationColorOptions { get; }

    /// <summary>
    /// The colour this client's annotations are drawn in, here and on the host it draws to.
    /// Unlike the other settings it is applied as soon as it is picked rather than when the
    /// settings pane closes, so the choice can be judged against a real drawing. Saving still
    /// happens on close, which is also where a reopened pane restores what was saved.
    /// </summary>
    public string AnnotationColor
    {
        get => annotationColor;
        set
        {
            if (SetProperty(ref annotationColor, AnnotationColors.Normalize(value)))
            {
                RefreshAnnotationColorSelection();
                RaisePropertyChanged(nameof(IsCustomAnnotationColor));
                Annotator.SetAnnotationColor(annotationColor);
            }
        }
    }

    /// <summary>
    /// True when the chosen colour is not one of the presets, which is what puts the ring on the
    /// custom swatch instead.
    /// </summary>
    public bool IsCustomAnnotationColor => !AnnotationColorOptions.Any(
        option => string.Equals(option.Color, AnnotationColor, StringComparison.Ordinal));

    public ICommand SelectAnnotationColorCommand { get; }

    /// <summary>
    /// Explains a colour on screen that is not the one selected here, which happens when an
    /// annotator ahead of this one already holds the chosen colour. Empty the rest of the time,
    /// which collapses the line.
    /// </summary>
    public string AnnotationColorNotice => Annotator.IsAnnotationColorReassigned
        ? $"In use by another annotator. Drawing in {DescribeAnnotationColor(Annotator.AnnotationColor)} for this session."
        : string.Empty;

    private static string DescribeAnnotationColor(string color)
    {
        var index = AnnotationColors.Palette
            .Select((paletteColor, paletteIndex) => (paletteColor, paletteIndex))
            .FirstOrDefault(entry => string.Equals(
                entry.paletteColor,
                color,
                StringComparison.Ordinal))
            .paletteIndex;
        return AnnotationColors.Palette.Contains(color, StringComparer.Ordinal)
            ? AnnotationColorNames[index].ToLowerInvariant()
            : color;
    }

    private void RefreshAnnotationColorSelection()
    {
        foreach (var option in AnnotationColorOptions)
        {
            option.IsSelected = string.Equals(
                option.Color,
                AnnotationColor,
                StringComparison.Ordinal);
        }
    }

    public string ConnectedAnnotatorCountLabel =>
        $"{ConnectedAnnotators.Count} annotator{(ConnectedAnnotators.Count == 1 ? string.Empty : "s")} connected";

    /// <summary>
    /// The bulk buttons only earn their place once a single row's own buttons cannot do the job.
    /// </summary>
    public bool HasMultipleConnectedAnnotators => ConnectedAnnotators.Count > 1;

    public bool AreAllAnnotatorsPaused =>
        ConnectedAnnotators.Count > 0 && ConnectedAnnotators.All(annotator => annotator.IsPaused);

    public string PauseAllActionLabel => AreAllAnnotatorsPaused ? "Resume all" : "Pause all";

    // Same Segoe MDL2 glyphs the per-annotator pause button uses, so the bulk action reads as
    // the same control applied to every row.
    public string PauseAllActionIcon => AreAllAnnotatorsPaused ? "" : "";

    public string FlyoutConnectionMessage => HasConnectedAnnotator
        ? ConnectedAnnotatorCountLabel
        : Annotator.ConnectionMessage;

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

    public bool CanSetHostAvailability => hostRelayClient is not null;

    public ICommand RefreshMonitorsCommand { get; }

    public ICommand ApproveAnnotatorCommand => approveAnnotatorCommand;

    public ICommand DisconnectAllConnectionsCommand => disconnectAllConnectionsCommand;

    public ICommand TogglePauseAllCommand => togglePauseAllCommand;

    public ICommand SetHostAvailabilityCommand => setHostAvailabilityCommand;

    public ICommand ToggleSettingsCommand { get; }

    public ICommand CloseSettingsCommand { get; }

    public ICommand TestServerConnectionCommand => testServerConnectionCommand;

    public ICommand ToggleAvailabilityMenuCommand { get; }

    public ICommand ClearServerPasswordCommand { get; }

    public ICommand ChangeServerPasswordCommand => changeServerPasswordCommand;

    public ICommand ApplyServerPasswordCommand => applyServerPasswordCommand;

    public ICommand CancelServerPasswordChangeCommand { get; }

    public ICommand IncrementMaximumAnnotatorsCommand => incrementMaximumAnnotatorsCommand;

    public ICommand DecrementMaximumAnnotatorsCommand => decrementMaximumAnnotatorsCommand;

    public async Task SetHostAvailabilityAsync(HostAvailability availability)
    {
        var previousAvailability = HostAvailability;
        if (previousAvailability != availability)
        {
            CancelAvailabilityRetry();
        }

        SetHostAvailabilitySilently(availability);
        SaveHostAvailabilityPreference(availability);
        await UpdateHostAvailabilityAsync(availability, previousAvailability);
    }

    public async Task InitializeAsync()
    {
        var annotatorInitialization = Annotator.InitializeAsync();
        if (hostRelayClient is not null)
        {
            try
            {
                var capabilities = await hostRelayClient.GetRelayCapabilitiesAsync();
                ServerPasswordRequired = capabilities.ServerPasswordRequired;
            }
            catch (Exception exception)
            {
                SetStatus($"Relay capabilities could not be loaded: {exception.Message}", true);
            }
        }

        await annotatorInitialization;
        if (HostAvailability == HostAvailability.Available && !HasHostSession)
        {
            await UpdateHostAvailabilityAsync(
                HostAvailability.Available,
                HostAvailability.Invisible);
        }
    }

    public async Task RestoreSessionsAsync()
    {
        var canRestoreHost = hostRelayClient?.Credential?.Role == ClientRole.Host;
        var canRestoreAnnotator = Annotator.HasRecoverableSession;
        if (canRestoreHost && canRestoreAnnotator)
        {
            HostConnectionMessage =
                "Saved host and annotator roles share this Windows profile; automatic recovery was skipped.";
            Annotator.ReportSharedProfileRecoverySkipped();
            return;
        }

        if (canRestoreHost && hostRelayClient is not null)
        {
            _ = await hostRelayClient.TryResumeSessionAsync();
        }

        if (canRestoreAnnotator)
        {
            await Annotator.RestoreSessionAsync();
        }
    }

    public void RefreshMonitors()
    {
        var previousDisplayId = SelectedMonitor?.Display.DisplayId
            ?? clientSettings?.Host.SelectedDisplayId;

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
        Annotator.HandleLocalDisplayConfigurationChanged();

        if (hostSessionId is null
            || hostRelayClient is null
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
            await hostRelayClient.UpdateHostDisplayAsync(SelectedMonitor.Display);
            SetStatus("Host display information updated.", false);
        }
        catch (Exception exception)
        {
            SetStatus($"Host display information could not be updated: {exception.Message}", true);
        }
    }

    public async Task ApplySelectedMonitorAsync()
    {
        if (hostSessionId is null || hostRelayClient is null || SelectedMonitor is null)
        {
            return;
        }

        try
        {
            overlayService.Show(SelectedMonitor);
            await hostRelayClient.UpdateHostDisplayAsync(SelectedMonitor.Display);
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
        annotatingTimer.Stop();
        annotatingTimer.Tick -= OnAnnotatingTimerTick;
        overlayService.StateChanged -= OnOverlayStateChanged;
        if (hostRelayClient is not null)
        {
            hostRelayClient.ConnectionStatusChanged -= OnHostConnectionStatusChanged;
            hostRelayClient.AnnotatorJoinRequested -= OnAnnotatorJoinRequested;
            hostRelayClient.AnnotatorJoinCancelled -= OnAnnotatorJoinCancelled;
            hostRelayClient.SessionApproved -= OnHostSessionApproved;
            hostRelayClient.PointerReceived -= OnPointerReceived;
            hostRelayClient.SessionEnded -= OnHostSessionEnded;
            RelayClientShutdown.Complete(hostRelayClient);
        }

        overlayService.Dispose();
        targetRegionService.UsageHintsShown -= OnUsageHintsShown;
        Annotator.PropertyChanged -= OnAnnotatorPropertyChanged;
        Annotator.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task CreateHostSessionAsync()
    {
        if (SelectedMonitor is null || hostRelayClient is null)
        {
            SetStatus("Select a connected monitor before becoming available.", isError: true);
            return;
        }

        try
        {
            overlayService.Show(SelectedMonitor);
            var response = await hostRelayClient.CreateHostSessionAsync(SelectedMonitor.Display);
            hostSessionId = response.SessionId;
            SetHostAvailabilitySilently(HostAvailability.Available);
            SetPendingAnnotator(null);
            RaiseHostSessionProperties();
            SetStatus("This host is available for access requests.", false);
        }
        catch (Exception exception)
        {
            overlayService.Hide();
            ClearHostSession(preserveAvailability: true);
            SetStatus($"The host could not become available: {exception.Message}", true);
        }
    }

    public async Task ApprovePendingAnnotatorAsync()
    {
        if (hostRelayClient is null || hostSessionId is null || pendingAnnotator is null)
        {
            return;
        }

        try
        {
            await hostRelayClient.ApproveAnnotatorAsync(
                hostSessionId,
                pendingAnnotator.ConnectionId);
            SetStatus($"Approved {pendingAnnotator.DisplayName}.", false);
            SetPendingAnnotator(null);
        }
        catch (Exception exception)
        {
            SetStatus($"The annotator could not be approved: {exception.Message}", true);
        }
    }

    public async Task RejectPendingAnnotatorAsync()
    {
        if (hostRelayClient is null || hostSessionId is null || pendingAnnotator is null)
        {
            return;
        }

        try
        {
            await hostRelayClient.RejectAnnotatorAsync(
                hostSessionId,
                pendingAnnotator.ConnectionId);
            SetStatus($"Declined {pendingAnnotator.DisplayName}.", false);
            SetPendingAnnotator(null);
        }
        catch (Exception exception)
        {
            SetStatus($"The annotator request could not be declined: {exception.Message}", true);
            throw;
        }
    }

    private async Task DisconnectAllConnectionsAsync()
    {
        if (hostRelayClient is null || !HasConnectedAnnotator)
        {
            return;
        }

        try
        {
            await hostRelayClient.DisconnectAllConnectionsAsync();
            SetPendingAnnotator(null);
            SetStatus("Disconnected all annotator connections.", false);
        }
        catch (Exception exception)
        {
            SetStatus($"Annotator connections could not be disconnected: {exception.Message}", true);
        }
    }

    private async Task UpdateHostAvailabilityAsync(
        HostAvailability requestedAvailability,
        HostAvailability previousAvailability)
    {
        await availabilityUpdateGate.WaitAsync();
        try
        {
            await UpdateHostAvailabilityCoreAsync(
                requestedAvailability,
                previousAvailability);
        }
        finally
        {
            availabilityUpdateGate.Release();
        }
    }

    private async Task UpdateHostAvailabilityCoreAsync(
        HostAvailability requestedAvailability,
        HostAvailability previousAvailability)
    {
        IsAvailabilityMenuOpen = false;
        if (hostRelayClient is null)
        {
            SetHostAvailabilitySilently(previousAvailability);
            return;
        }

        try
        {
            if (requestedAvailability == HostAvailability.Invisible
                && !HasHostSession)
            {
                SetHostAvailabilitySilently(HostAvailability.Invisible);
                CancelAvailabilityRetry();
                SetStatus("This host is invisible to annotators.", false);
                return;
            }

            if (requestedAvailability == HostAvailability.Available
                && !HasHostSession)
            {
                await CreateHostSessionAsync();
                if (!HasHostSession)
                {
                    SetHostAvailabilitySilently(HostAvailability.Available);
                    ScheduleAvailabilityRetry();
                }
                else
                {
                    CancelAvailabilityRetry();
                }
                return;
            }

            var isAvailable = await hostRelayClient.SetHostDiscoverableAsync(
                requestedAvailability == HostAvailability.Available);
            SetHostAvailabilitySilently(
                isAvailable ? HostAvailability.Available : HostAvailability.Invisible);
            CancelAvailabilityRetry();
            SetStatus(
                isAvailable
                    ? "This host is available for access requests."
                    : "This host is invisible to annotators.",
                false);
        }
        catch (Exception exception)
        {
            SetHostAvailabilitySilently(requestedAvailability);
            ScheduleAvailabilityRetry();
            SetStatus($"Host availability could not be changed: {exception.Message}", true);
        }
    }

    private void OnOverlayStateChanged(object? sender, OverlayStateChangedEventArgs e)
    {
        IsOverlayVisible = e.IsVisible;
        SetStatus(e.Message, e.IsError);
    }

    private void OnHostConnectionStatusChanged(
        object? sender,
        RelayConnectionStatusChangedEventArgs e)
    {
        HostConnectionMessage = e.Message;
        RaisePropertyChanged(nameof(IsServerAvailable));
        RaisePropertyChanged(nameof(ServerConnectionGuidance));
        RaisePropertyChanged(nameof(EmptyClientListMessage));
        RaiseAvailabilityProperties();
    }

    private void OnAnnotatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is nameof(AnnotatorViewModel.IsSessionApproved)
            or nameof(AnnotatorViewModel.IsJoinPending))
        {
            approveAnnotatorCommand.RaiseCanExecuteChanged();
        }

        if (e.PropertyName == nameof(AnnotatorViewModel.ConnectionMessage))
        {
            RaisePropertyChanged(nameof(FlyoutConnectionMessage));
        }

        if (e.PropertyName == nameof(AnnotatorViewModel.IsAnnotationColorReassigned))
        {
            RaisePropertyChanged(nameof(AnnotationColorNotice));
        }
    }

    private void OnAnnotatorJoinRequested(object? sender, AnnotatorJoinRequestedEventArgs e)
    {
        SetPendingAnnotator(e.Annotator);
        SetStatus($"{e.Annotator.DisplayName} is waiting for approval.", false);
    }

    private void OnHostSessionApproved(object? sender, RelaySessionStateEventArgs e)
    {
        var annotatorDisconnected = HasConnectedAnnotator && !e.State.Approved;
        var descriptors = e.State.ConnectedAnnotators ?? [];
        if (e.State.Approved && descriptors.Length == 0)
        {
            descriptors = [new ConnectedAnnotatorDescriptor("Connected annotator")];
        }

        ApplyConnectedAnnotators(descriptors);
        RaiseConnectedAnnotatorProperties();
        HasConnectedAnnotator = ConnectedAnnotators.Count > 0;
        if (hostRelayClient?.Credential?.Role == ClientRole.Host)
        {
            hostSessionId = e.State.SessionId;
            SetHostAvailabilitySilently(
                e.State.HostDiscoverable
                    ? HostAvailability.Available
                    : HostAvailability.Invisible);
            if (e.State.HostDisplay is not null)
            {
                var restoredMonitor = Monitors.FirstOrDefault(
                    monitor => string.Equals(
                        monitor.Display.DisplayId,
                        e.State.HostDisplay.DisplayId,
                        StringComparison.OrdinalIgnoreCase));
                if (restoredMonitor is null)
                {
                    RaiseHostSessionProperties();
                    SetStatus(
                        "Host presence resumed, but its monitor is not connected.",
                        true);
                    return;
                }

                SelectedMonitor = restoredMonitor;
                overlayService.Show(restoredMonitor);
            }

            RaiseHostSessionProperties();
        }

        SetStatus(
            HasConnectedAnnotator
                ? $"Receiving pointers from {ConnectedAnnotators.Count} connected annotator{(ConnectedAnnotators.Count == 1 ? string.Empty : "s")}."
                : annotatorDisconnected && HostAvailability == HostAvailability.Available
                    ? "Annotator disconnected. This host remains available."
                    : annotatorDisconnected
                        ? "Annotator disconnected. This host remains invisible."
                        : HostAvailability == HostAvailability.Available
                            ? "This host is available for access requests."
                            : "This host is invisible to annotators.",
            false);
    }

    /// <summary>
    /// Reuses the row that already stands for an annotator instead of replacing it, so the
    /// annotating indicator does not blink off every time the relay resends session state.
    /// </summary>
    private void ApplyConnectedAnnotators(IReadOnlyList<ConnectedAnnotatorDescriptor> descriptors)
    {
        var unclaimed = ConnectedAnnotators.ToList();
        var rows = new List<ConnectedAnnotatorViewModel>(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            // An annotator the relay named is matched by that name; an unnamed one can only be
            // matched to another unnamed row, and claimed rows are never matched twice.
            var match = unclaimed.FirstOrDefault(
                annotator => string.Equals(
                    annotator.AnnotatorId,
                    descriptor.AnnotatorId,
                    StringComparison.Ordinal));
            if (match is null)
            {
                rows.Add(new ConnectedAnnotatorViewModel(
                    descriptor,
                    ToggleAnnotatorPausedAsync,
                    DisconnectAnnotatorAsync));
                continue;
            }

            unclaimed.Remove(match);
            match.Update(descriptor);
            rows.Add(match);
        }

        foreach (var departed in unclaimed)
        {
            lastPointerReceivedAt.Remove(departed.AnnotatorId);
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var currentIndex = ConnectedAnnotators.IndexOf(rows[index]);
            if (currentIndex < 0)
            {
                ConnectedAnnotators.Insert(index, rows[index]);
            }
            else if (currentIndex != index)
            {
                ConnectedAnnotators.Move(currentIndex, index);
            }
        }

        // Everything the relay no longer reports has been pushed past the rows it does.
        while (ConnectedAnnotators.Count > rows.Count)
        {
            ConnectedAnnotators.RemoveAt(ConnectedAnnotators.Count - 1);
        }

        UpdateAnnotatingIndicators();
    }

    private void RaiseConnectedAnnotatorProperties()
    {
        RaisePropertyChanged(nameof(ConnectedAnnotatorCountLabel));
        RaisePropertyChanged(nameof(HasMultipleConnectedAnnotators));
        RaisePropertyChanged(nameof(PauseAllActionLabel));
        RaisePropertyChanged(nameof(PauseAllActionIcon));
        RaisePropertyChanged(nameof(FlyoutConnectionMessage));
        togglePauseAllCommand.RaiseCanExecuteChanged();
    }

    private async Task ToggleAnnotatorPausedAsync(ConnectedAnnotatorViewModel annotator)
    {
        if (hostRelayClient is null || !HasHostSession)
        {
            return;
        }

        var paused = !annotator.IsPaused;
        try
        {
            await hostRelayClient.SetAnnotatorPausedAsync(annotator.AnnotatorId, paused);
            SetStatus(
                paused
                    ? $"Paused {annotator.DisplayName}."
                    : $"{annotator.DisplayName} can annotate again.",
                false);
        }
        catch (Exception exception)
        {
            SetStatus($"{annotator.DisplayName} could not be paused: {exception.Message}", true);
        }
    }

    private async Task SetAllAnnotatorsPausedAsync(bool paused)
    {
        if (hostRelayClient is null || !HasConnectedAnnotator || !HasHostSession)
        {
            return;
        }

        try
        {
            await hostRelayClient.SetAnnotatorPausedAsync(null, paused);
            SetStatus(
                paused
                    ? "Paused every connected annotator."
                    : "Every connected annotator can annotate again.",
                false);
        }
        catch (Exception exception)
        {
            SetStatus($"The annotators could not be paused: {exception.Message}", true);
        }
    }

    private async Task DisconnectAnnotatorAsync(ConnectedAnnotatorViewModel annotator)
    {
        if (hostRelayClient is null || !HasHostSession)
        {
            return;
        }

        try
        {
            await hostRelayClient.DisconnectAnnotatorAsync(annotator.AnnotatorId);
            SetStatus($"Disconnected {annotator.DisplayName}.", false);
        }
        catch (Exception exception)
        {
            SetStatus(
                $"{annotator.DisplayName} could not be disconnected: {exception.Message}",
                true);
        }
    }

    private void OnAnnotatingTimerTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateAnnotatingIndicators();
    }

    private void UpdateAnnotatingIndicators()
    {
        var now = Environment.TickCount64;
        var anyAnnotating = false;
        foreach (var annotator in ConnectedAnnotators)
        {
            var annotating = lastPointerReceivedAt.TryGetValue(
                    annotator.AnnotatorId,
                    out var lastEvent)
                && now - lastEvent <= AnnotatingIdleMilliseconds;
            annotator.IsAnnotating = annotating;
            anyAnnotating |= annotating;
        }

        // The timer exists only to notice a stream going quiet, so it runs only while there is
        // one to notice.
        if (anyAnnotating)
        {
            annotatingTimer.Start();
        }
        else
        {
            annotatingTimer.Stop();
        }
    }

    private void MarkAnnotating(string? annotatorId)
    {
        if (string.IsNullOrEmpty(annotatorId))
        {
            return;
        }

        lastPointerReceivedAt[annotatorId] = Environment.TickCount64;
        UpdateAnnotatingIndicators();
    }

    private async void OnPointerReceived(object? sender, RelayPointerEventArgs e)
    {
        try
        {
            var activeSessionId = hostSessionId;
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

            MarkAnnotating(e.PointerEvent.AnnotatorId);

            if (!string.Equals(
                    hostSessionId,
                    activeSessionId,
                    StringComparison.Ordinal))
            {
                return;
            }

            var displayed = overlayService.ShowPointer(e.PointerEvent);
            if (displayed && hostRelayClient is not null)
            {
                await hostRelayClient.AcknowledgePointerAsync(
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

    private void OnHostSessionEnded(object? sender, RelaySessionEndedEventArgs e)
    {
        var retryAvailability = HostAvailability == HostAvailability.Available;
        overlayService.Hide();
        ClearHostSession(preserveAvailability: retryAvailability);
        if (retryAvailability)
        {
            ScheduleAvailabilityRetry();
        }
        SetStatus(e.Reason, e.Expired);
    }

    private void SetPendingAnnotator(AnnotatorDescriptor? annotator)
    {
        pendingAnnotator = annotator;
        RaisePropertyChanged(nameof(PendingAnnotatorName));
        RaisePropertyChanged(nameof(PendingAnnotatorProfilePicturePng));
        RaisePropertyChanged(nameof(HasPendingAnnotator));
        approveAnnotatorCommand.RaiseCanExecuteChanged();
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
        ServerVersionLabel = result.IsSuccessful && !string.IsNullOrWhiteSpace(result.ServerVersion)
            ? $"Server version {result.ServerVersion}"
            : string.Empty;
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

            var addressIsNew = !string.Equals(
                clientSettings.Server.BaseUrl,
                requestedServerAddress,
                StringComparison.Ordinal);
            PersistSettings(requestedServerAddress);
            pendingRelayReinitialization = !string.Equals(
                activeServerAddress,
                requestedServerAddress,
                StringComparison.Ordinal);
            ServerConnectionTestMessage = string.Empty;
            IsServerAddressVerified = true;
            RaiseServerAddressCommandState();
            SetStatus(
                addressIsNew
                    ? "Server connection verified and address saved."
                    : "Server connection verified.",
                false);
        }
        catch (Exception exception)
        {
            SetStatus($"Settings could not be saved: {exception.Message}", true);
            ServerConnectionTestMessage = $"The address could not be saved: {exception.Message}";
        }
    }

    private void OnAnnotatorJoinCancelled(object? sender, AnnotatorJoinCancelledEventArgs e)
    {
        if (!string.Equals(
                pendingAnnotator?.ConnectionId,
                e.AnnotatorConnectionId,
                StringComparison.Ordinal))
        {
            return;
        }

        SetPendingAnnotator(null);
        SetStatus("The annotator withdrew its connection request.", false);
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
        ServerVersionLabel = string.Empty;
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

        var hasActiveConnections = HasConnectedAnnotator
            || HasPendingAnnotator
            || Annotator.IsSessionApproved
            || Annotator.IsJoinPending;
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
            MaximumAnnotatorConnections,
            IsLaunchAtStartup,
            SelectedMonitor?.Display.DisplayId,
            ShowUsageHints,
            HostAvailability == HostAvailability.Available,
            DrawingOpacityPercent,
            AnnotationColor);
        Annotator.SetUsageHintsState(ShowUsageHints, hasShownUsageHints);
        Annotator.SetDrawingOpacityPercent(DrawingOpacityPercent);
        // The annotation colour is deliberately absent: it was already applied the moment it was
        // picked, so saving it here would only repeat that.
        startupRegistrationService?.SetEnabled(IsLaunchAtStartup);
        RaiseServerAddressCommandState();
    }

    private Task ApplyActiveClientSettingsAsync() => Task.WhenAll(
        hostRelayClient?.ApplyClientSettingsAsync(
            UserName,
            ProfilePicturePath,
            MaximumAnnotatorConnections) ?? Task.CompletedTask,
        Annotator.ApplyClientSettingsAsync(
            UserName,
            ProfilePicturePath,
            MaximumAnnotatorConnections));

    private void ResetServerAddressDraft()
    {
        ServerAddressInput = RemoveHttpsPrefix(clientSettings?.Server.BaseUrl ?? string.Empty);
        ServerConnectionTestMessage = string.Empty;
        ServerVersionLabel = string.Empty;
    }

    private void RaiseServerAddressCommandState() =>
        testServerConnectionCommand.RaiseCanExecuteChanged();

    private void ResetSettingsDraft()
    {
        if (clientSettings is null)
        {
            return;
        }

        ServerAddressInput = RemoveHttpsPrefix(clientSettings.Server.BaseUrl);
        UserName = clientSettings.Profile.UserName;
        ProfilePicturePath = clientSettings.Profile.PicturePath;
        MaximumAnnotatorConnections = clientSettings.Host.MaximumAnnotatorConnections;
        IsLaunchAtStartup = startupRegistrationService?.IsEnabled
            ?? clientSettings.Startup.LaunchAtStartup;
        ShowUsageHints = clientSettings.Pointer.ShowUsageHints;
        DrawingOpacityPercent = clientSettings.Pointer.DrawingOpacityPercent;
        AnnotationColor = clientSettings.Pointer.AnnotationColor;

        var savedMonitor = Monitors.FirstOrDefault(
            monitor => string.Equals(
                monitor.Display.DisplayId,
                clientSettings.Host.SelectedDisplayId,
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

    private void ClearHostSession(bool preserveAvailability = false)
    {
        hostSessionId = null;
        ConnectedAnnotators.Clear();
        lastPointerReceivedAt.Clear();
        annotatingTimer.Stop();
        RaiseConnectedAnnotatorProperties();
        HasConnectedAnnotator = false;
        if (!preserveAvailability)
        {
            SetHostAvailabilitySilently(HostAvailability.Invisible);
        }
        SetPendingAnnotator(null);
        RaiseHostSessionProperties();
    }

    private void SetHostAvailabilitySilently(HostAvailability availability)
    {
        HostAvailability = availability;
    }

    private void SaveHostAvailabilityPreference(HostAvailability availability)
    {
        if (clientSettings is null)
        {
            return;
        }

        try
        {
            clientSettings.SaveHostAvailability(
                availability == HostAvailability.Available);
        }
        catch (Exception exception)
        {
            SetStatus($"Host availability preference could not be saved: {exception.Message}", true);
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
        var selectedAvailability = HostAvailability;
        var updateNeeded = selectedAvailability == HostAvailability.Available
            ? !HasHostSession
            : HasHostSession;
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
        HostAvailability availability)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested
                   && HostAvailability == availability)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                if (cancellationToken.IsCancellationRequested
                    || HostAvailability != availability)
                {
                    return;
                }

                await UpdateHostAvailabilityAsync(
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
            if (hostRelayClient is not null)
            {
                await hostRelayClient.SetServerPasswordKeyAsync(key);
            }

            await Annotator.SetServerPasswordKeyAsync(key);
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

    private void RaiseHostSessionProperties()
    {
        RaisePropertyChanged(nameof(HasHostSession));
        RaisePropertyChanged(nameof(CanSelectMonitor));
        RaisePropertyChanged(nameof(CanSetHostAvailability));
        approveAnnotatorCommand.RaiseCanExecuteChanged();
        disconnectAllConnectionsCommand.RaiseCanExecuteChanged();
        setHostAvailabilityCommand.RaiseCanExecuteChanged();
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsError = isError;
    }
}

public enum HostAvailability
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
