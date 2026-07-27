using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using RemoteAnnotate.Client.Configuration;
using RemoteAnnotate.Client.Native;
using RemoteAnnotate.Client.Overlays;
using RemoteAnnotate.Client.Services;
using RemoteAnnotate.Client.ViewModels;

namespace RemoteAnnotate.Client.Views;

public partial class MainWindow : Window
{
    private const string RepositoryUrl = "https://github.com/MatthiasHeim3D/remote-annotate";
    private const double ExpandedHeight = 520d;
    private const double AvailableClientsBaseHeight = 212d;
    private const double AvailableClientRowHeight = 64d;

    // With nothing to list, the panel draws its empty-state icon and message instead of a row,
    // which needs more room than a single client would.
    private const double AvailableClientsEmptyHeight = 244d;

    // The flyout is sized rather than measured, so these track what the connected-annotator panel
    // actually draws: 108 of window chrome, its heading, then one 52 row per annotator. The
    // bulk-action row only exists once a second annotator makes per-row clicking repetitive.
    private const double ConnectedClientsBaseHeight = 200d;
    private const double ConnectedClientRowHeight = 52d;
    private const double ConnectedClientsActionRowHeight = 44d;

    // A connected host is one session row under a heading — the same shape as a single connected
    // annotator — so the two panels stand at exactly the same height rather than drifting apart.
    internal const double AnnotatorSessionHeight = ConnectedClientsBaseHeight;
    private const int MaximumVisibleClientRows = 4;
    private GlobalHotKeyRegistration? hotKeyRegistration;
    private HwndSource? source;
    private readonly SystemTrayIcon trayIcon;
    private readonly MainWindowViewModel viewModel;
    private readonly IClientAuditLog? auditLog;
    private ConnectionApprovalWindow? approvalWindow;
    private bool suppressAutoHide;

    public MainWindow(IClientAuditLog? auditLog = null)
    {
        InitializeComponent();
        this.auditLog = auditLog;

        var monitorService = new MonitorService();
        var coordinateMapper = new DisplayCoordinateMapper();
        var overlayService = new HostOverlayService(monitorService, coordinateMapper);
        var targetRegionService = new TargetRegionService();
        var settings = ClientSettings.Load();
        var clientInstanceIdProvider = new ClientInstanceIdProvider();
        var dataProtector = new DpapiDataProtector();
        var protectedSessionStore = new ProtectedSessionStore(dataProtector, auditLog);
        var serverPasswordStore = new ProtectedServerPasswordStore(dataProtector, auditLog);
        // Loaded before the connections are built so the first connect already presents it.
        settings.Server.PasswordKey = serverPasswordStore.Load();
        IRelayClient? hostRelayClient = null;
        IRelayClient? annotatorRelayClient = null;
        if (!string.IsNullOrWhiteSpace(settings.Server.BaseUrl))
        {
            hostRelayClient = new SignalRRelayClient(
                settings,
                clientInstanceIdProvider,
                expectedRole: RemoteAnnotate.Contracts.Messages.ClientRole.Host,
                sessionStore: protectedSessionStore,
                auditLog: auditLog);
            annotatorRelayClient = new SignalRRelayClient(
                settings,
                clientInstanceIdProvider,
                expectedRole: RemoteAnnotate.Contracts.Messages.ClientRole.Annotator,
                sessionStore: protectedSessionStore,
                auditLog: auditLog);
        }
        viewModel = new MainWindowViewModel(
            monitorService,
            overlayService,
            targetRegionService,
            hostRelayClient,
            annotatorRelayClient,
            settings.Pointer.DefaultTtlMilliseconds,
            settings,
            new StartupRegistrationService(),
            serverConnectionTester: null,
            serverPasswordStore: serverPasswordStore);
        DataContext = viewModel;

        trayIcon = new SystemTrayIcon(ShowFromTray, ExitFromTray);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.Annotator.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.Annotator.AvailableHosts.CollectionChanged += OnClientCollectionChanged;
        viewModel.ConnectedAnnotators.CollectionChanged += OnClientCollectionChanged;
        viewModel.ServerAddressChangeRequested += OnServerAddressChangeRequested;
        viewModel.RelayReinitializationRequested += OnRelayReinitializationRequested;
        StateChanged += OnWindowStateChanged;
        Loaded += OnLoaded;

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        UpdateFlyoutHeight();
    }

    internal bool RequiresInitialSetup => viewModel.IsServerConfigurationMissing;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(handle);
        source?.AddHook(WindowMessageHook);

        try
        {
            hotKeyRegistration = new GlobalHotKeyRegistration(handle);
        }
        catch (Win32Exception exception)
        {
            viewModel.Annotator.ReportHotKeyRegistrationFailure(exception.Message);
        }
    }

    private nint WindowMessageHook(
        nint window,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        _ = window;
        _ = wordParameter;
        _ = longParameter;
        _ = handled;

        if (message == NativeMethods.WmDisplayChange)
        {
            _ = Dispatcher.InvokeAsync(
                viewModel.HandleDisplayConfigurationChangedAsync,
                DispatcherPriority.Background);
        }

        if (message == NativeMethods.WmHotKey
            && wordParameter.ToInt32() == GlobalHotKeyRegistration.TogglePointerHotKeyId)
        {
            handled = true;
            viewModel.Annotator.ToggleAnnotatingMode();
        }

        return 0;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Loaded -= OnLoaded;
        StateChanged -= OnWindowStateChanged;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.Annotator.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.Annotator.AvailableHosts.CollectionChanged -= OnClientCollectionChanged;
        viewModel.ConnectedAnnotators.CollectionChanged -= OnClientCollectionChanged;
        viewModel.ServerAddressChangeRequested -= OnServerAddressChangeRequested;
        viewModel.RelayReinitializationRequested -= OnRelayReinitializationRequested;
        source?.RemoveHook(WindowMessageHook);
        source = null;
        hotKeyRegistration?.Dispose();
        hotKeyRegistration = null;
        approvalWindow?.Close();
        approvalWindow = null;
        trayIcon.Dispose();
        viewModel.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        PositionFlyout();
        await viewModel.RestoreSessionsAsync();
        await viewModel.InitializeAsync();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == System.Windows.WindowState.Minimized)
        {
            Hide();
            ShowInTaskbar = false;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, viewModel)
            && e.PropertyName == nameof(MainWindowViewModel.HasPendingAnnotator))
        {
            if (viewModel.HasPendingAnnotator)
            {
                ShowConnectionApprovalPrompt();
            }
            else
            {
                approvalWindow?.Close();
            }
        }

        if (ReferenceEquals(sender, viewModel)
            && e.PropertyName == nameof(MainWindowViewModel.ServerPasswordInput)
            && viewModel.ServerPasswordInput.Length == 0
            && ServerPasswordBox.Password.Length > 0)
        {
            ServerPasswordBox.Clear();
        }

        if (ReferenceEquals(sender, viewModel)
            && e.PropertyName == nameof(MainWindowViewModel.IsChangingServerPassword)
            && viewModel.IsChangingServerPassword)
        {
            // The same property change reveals the box, so focus has to wait for that layout pass.
            _ = Dispatcher.BeginInvoke(
                () => ServerPasswordBox.Focus(),
                DispatcherPriority.Input);
        }

        if ((ReferenceEquals(sender, viewModel)
                && e.PropertyName is nameof(MainWindowViewModel.IsSettingsOpen)
                    or nameof(MainWindowViewModel.HasConnectedAnnotator))
            || (ReferenceEquals(sender, viewModel.Annotator)
                && e.PropertyName is nameof(AnnotatorViewModel.IsSessionApproved)
                    or nameof(AnnotatorViewModel.IsJoinPending)))
        {
            UpdateFlyoutHeight();
        }

        var status = viewModel.Annotator.IsAnnotating
            ? "Annotating active"
            : viewModel.Annotator.IsSessionApproved
                ? "Annotator connected"
                : viewModel.HasConnectedAnnotator
                    ? "Receiving pointers"
                    : viewModel.HasHostSession
                    ? viewModel.HostAvailability == HostAvailability.Available
                        ? "Host available"
                        : "Host invisible"
                    : "Inactive";
        trayIcon.SetStatus(status);
    }

    private void UpdateFlyoutHeight()
    {
        Height = viewModel.IsSettingsOpen
            ? ExpandedHeight
            : viewModel.HasConnectedAnnotator
                ? CalculateConnectedClientsHeight(viewModel.ConnectedAnnotators.Count)
                : (viewModel.Annotator.IsSessionApproved || viewModel.Annotator.IsJoinPending)
                    ? AnnotatorSessionHeight
                    : CalculateAvailableClientsHeight(
                        viewModel.Annotator.AvailableHosts.Count);
        if (IsLoaded)
        {
            PositionFlyout();
        }
    }

    private void OnClientCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateFlyoutHeight();
    }

    internal static double CalculateClientListHeight(
        double baseHeight,
        double rowHeight,
        int clientCount) => baseHeight
        + (Math.Clamp(clientCount, 1, MaximumVisibleClientRows) - 1) * rowHeight;

    /// <summary>
    /// Height of the discovery list, which falls back to its roomier empty state when there is
    /// no client to show.
    /// </summary>
    internal static double CalculateAvailableClientsHeight(int clientCount) => clientCount == 0
        ? AvailableClientsEmptyHeight
        : CalculateClientListHeight(
            AvailableClientsBaseHeight,
            AvailableClientRowHeight,
            clientCount);

    /// <summary>
    /// Height of the connected-annotator panel, which unlike the discovery list grows by a row
    /// of bulk actions the moment a second annotator appears.
    /// </summary>
    internal static double CalculateConnectedClientsHeight(int clientCount) =>
        CalculateClientListHeight(
            ConnectedClientsBaseHeight,
            ConnectedClientRowHeight,
            clientCount)
        + (clientCount > 1 ? ConnectedClientsActionRowHeight : 0d);

    private void ShowFromTray()
    {
        _ = Dispatcher.InvokeAsync(
            () =>
            {
                if (IsVisible && IsActive)
                {
                    Hide();
                    return;
                }

                ShowAndActivate();
            });
    }

    internal void ShowAndActivate()
    {
        Show();
        WindowState = System.Windows.WindowState.Normal;
        PositionFlyout();
        Activate();
        Focus();

        // Showing a window that was hidden when the display scale changed resizes it a beat after
        // Show returns, which leaves the placement above measuring the old size. Corner it again
        // once that has settled.
        _ = Dispatcher.InvokeAsync(PositionFlyout, DispatcherPriority.Loaded);
    }

    private void ExitFromTray()
    {
        _ = Dispatcher.InvokeAsync(Close);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (!suppressAutoHide)
        {
            viewModel.IsAvailabilityMenuOpen = false;
            Hide();
        }
    }

    private void OnPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _ = sender;
        _ = e;
        if (viewModel.IsAvailabilityMenuOpen
            && !ProfileButton.IsMouseOver
            && !AvailabilityPanel.IsMouseOver)
        {
            viewModel.IsAvailabilityMenuOpen = false;
        }
    }

    private void OnServerAddressChangeRequested(
        object? sender,
        ServerAddressChangeRequestedEventArgs e)
    {
        _ = sender;
        suppressAutoHide = true;
        try
        {
            var result = MessageBox.Show(
                this,
                "Changing the server disconnects the active session. Continue?",
                "Change Remote Annotate server",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            e.Approved = result == MessageBoxResult.Yes;
        }
        finally
        {
            suppressAutoHide = false;
        }
    }

    private void OnRelayReinitializationRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                var replacement = new MainWindow(auditLog);
                Application.Current.MainWindow = replacement;
                replacement.Show();
                replacement.Activate();
                Close();
            },
            DispatcherPriority.Background);
    }

    private void OnBrowseProfilePicture(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        suppressAutoHide = true;
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose a profile picture",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };
            if (dialog.ShowDialog(this) == true)
            {
                viewModel.ProfilePicturePath = dialog.FileName;
            }
        }
        finally
        {
            suppressAutoHide = false;
            Activate();
        }
    }

    /// <summary>
    /// Opens the system colour picker for the annotation colour. WPF ships no colour dialog, and
    /// the Windows Forms one is already available to this project; it also gives the user the
    /// familiar custom-colour panel rather than a hex field to type into.
    /// </summary>
    private void OnChooseAnnotationColor(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        suppressAutoHide = true;
        try
        {
            var current = AnnotationPalette.ToColor(viewModel.AnnotationColor);
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
                FullOpen = true,
                AnyColor = true,
                SolidColorOnly = true,
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                viewModel.AnnotationColor = AnnotationPalette.ToAnnotationColor(
                    dialog.Color.R,
                    dialog.Color.G,
                    dialog.Color.B);
            }
        }
        finally
        {
            suppressAutoHide = false;
            Activate();
        }
    }

    /// <summary>
    /// PasswordBox deliberately does not expose its value as a bindable property, so the draft
    /// is pushed to the view model here. It is cleared as soon as settings are saved.
    /// </summary>
    private void OnServerPasswordChanged(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        viewModel.ServerPasswordInput = ServerPasswordBox.Password;
    }

    private void OnOpenRepository(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        using var repositoryProcess = Process.Start(
            new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });
    }

    private async void OnReceivingScreenSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        await viewModel.ApplySelectedMonitorAsync();
    }

    private void ShowConnectionApprovalPrompt()
    {
        approvalWindow?.Close();
        approvalWindow = new ConnectionApprovalWindow(
            viewModel.PendingAnnotatorName,
            viewModel.PendingAnnotatorProfilePicturePng,
            viewModel.ApprovePendingAnnotatorAsync,
            viewModel.RejectPendingAnnotatorAsync);
        approvalWindow.Closed += (_, _) => approvalWindow = null;
        approvalWindow.Show();
    }

    private void PositionFlyout() => FlyoutPlacement.PlaceInBottomCorner(this);

    /// <summary>
    /// Anything that resizes the window leaves it hanging off the corner it was placed against,
    /// including the rescale Windows applies after a display-scale change. Re-cornering on every
    /// size change keeps that invariant without each caller having to remember it. Placement only
    /// moves the window, so this cannot feed back into itself.
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (IsVisible)
        {
            PositionFlyout();
        }
    }

    /// <summary>
    /// Windows moves the window when the display scale changes, which leaves it off the corner.
    /// The flyout is re-cornered on every show, so this only matters while it is open.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded)
        {
            PositionFlyout();
        }
    }
}
