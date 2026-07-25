using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using RemotePointer.Client.Configuration;
using RemotePointer.Client.Native;
using RemotePointer.Client.Services;
using RemotePointer.Client.ViewModels;

namespace RemotePointer.Client.Views;

public partial class MainWindow : Window
{
    private const string RepositoryUrl = "https://github.com/MatthiasHeim3D/remote-pointer";
    private const double ExpandedHeight = 520d;
    private const double SenderSessionHeight = 200d;
    private const double AvailableClientsBaseHeight = 244d;
    private const double AvailableClientRowHeight = 64d;
    private const double ConnectedClientsBaseHeight = 306d;
    private const double ConnectedClientRowHeight = 54d;
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
        var overlayService = new ReceiverOverlayService(monitorService, coordinateMapper);
        var targetRegionService = new TargetRegionService();
        var settings = ClientSettings.Load();
        var clientInstanceIdProvider = new ClientInstanceIdProvider();
        var dataProtector = new DpapiDataProtector();
        var protectedSessionStore = new ProtectedSessionStore(dataProtector, auditLog);
        var serverPasswordStore = new ProtectedServerPasswordStore(dataProtector, auditLog);
        // Loaded before the connections are built so the first connect already presents it.
        settings.Server.PasswordKey = serverPasswordStore.Load();
        IRelayClient? receiverRelayClient = null;
        IRelayClient? presenterRelayClient = null;
        if (!string.IsNullOrWhiteSpace(settings.Server.BaseUrl))
        {
            receiverRelayClient = new SignalRRelayClient(
                settings,
                clientInstanceIdProvider,
                expectedRole: RemotePointer.Contracts.Messages.ClientRole.Receiver,
                sessionStore: protectedSessionStore,
                auditLog: auditLog);
            presenterRelayClient = new SignalRRelayClient(
                settings,
                clientInstanceIdProvider,
                expectedRole: RemotePointer.Contracts.Messages.ClientRole.Presenter,
                sessionStore: protectedSessionStore,
                auditLog: auditLog);
        }
        viewModel = new MainWindowViewModel(
            monitorService,
            overlayService,
            targetRegionService,
            receiverRelayClient,
            presenterRelayClient,
            settings.Pointer.DefaultTtlMilliseconds,
            settings,
            new StartupRegistrationService(),
            serverConnectionTester: null,
            serverPasswordStore: serverPasswordStore);
        DataContext = viewModel;

        trayIcon = new SystemTrayIcon(ShowFromTray, ExitFromTray);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.Presenter.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.Presenter.AvailableReceivers.CollectionChanged += OnClientCollectionChanged;
        viewModel.ConnectedPresenters.CollectionChanged += OnClientCollectionChanged;
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
            viewModel.Presenter.ReportHotKeyRegistrationFailure(exception.Message);
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
            viewModel.Presenter.TogglePointingMode();
        }

        return 0;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Loaded -= OnLoaded;
        StateChanged -= OnWindowStateChanged;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.Presenter.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.Presenter.AvailableReceivers.CollectionChanged -= OnClientCollectionChanged;
        viewModel.ConnectedPresenters.CollectionChanged -= OnClientCollectionChanged;
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
            && e.PropertyName == nameof(MainWindowViewModel.HasPendingPresenter))
        {
            if (viewModel.HasPendingPresenter)
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

        if ((ReferenceEquals(sender, viewModel)
                && e.PropertyName is nameof(MainWindowViewModel.IsSettingsOpen)
                    or nameof(MainWindowViewModel.HasConnectedPresenter))
            || (ReferenceEquals(sender, viewModel.Presenter)
                && e.PropertyName is nameof(PresenterViewModel.IsSessionApproved)
                    or nameof(PresenterViewModel.IsJoinPending)))
        {
            UpdateFlyoutHeight();
        }

        var status = viewModel.Presenter.IsPointing
            ? "Pointing active"
            : viewModel.Presenter.IsSessionApproved
                ? "Presenter connected"
                : viewModel.HasConnectedPresenter
                    ? "Receiving pointers"
                    : viewModel.HasReceiverSession
                    ? viewModel.ReceiverAvailability == ReceiverAvailability.Available
                        ? "Receiver available"
                        : "Receiver invisible"
                    : "Inactive";
        trayIcon.SetStatus(status);
    }

    private void UpdateFlyoutHeight()
    {
        Height = viewModel.IsSettingsOpen
            ? ExpandedHeight
            : viewModel.HasConnectedPresenter
                ? CalculateClientListHeight(
                    ConnectedClientsBaseHeight,
                    ConnectedClientRowHeight,
                    viewModel.ConnectedPresenters.Count)
                : (viewModel.Presenter.IsSessionApproved || viewModel.Presenter.IsJoinPending)
                    ? SenderSessionHeight
                    : CalculateClientListHeight(
                        AvailableClientsBaseHeight,
                        AvailableClientRowHeight,
                        viewModel.Presenter.AvailableReceivers.Count);
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
                "Change Remote Pointer server",
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
            viewModel.PendingPresenterName,
            viewModel.PendingPresenterProfilePicturePng,
            viewModel.ApprovePendingPresenterAsync,
            viewModel.RejectPendingPresenterAsync);
        approvalWindow.Closed += (_, _) => approvalWindow = null;
        approvalWindow.Show();
    }

    private void PositionFlyout()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - Height - 12;
    }
}
