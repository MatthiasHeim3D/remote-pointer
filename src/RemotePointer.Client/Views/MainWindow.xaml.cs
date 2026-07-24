using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.ComponentModel;
using Microsoft.Win32;
using RemotePointer.Client.Configuration;
using RemotePointer.Client.Native;
using RemotePointer.Client.Services;
using RemotePointer.Client.ViewModels;

namespace RemotePointer.Client.Views;

public partial class MainWindow : Window
{
    private GlobalHotKeyRegistration? hotKeyRegistration;
    private HwndSource? source;
    private readonly SystemTrayIcon trayIcon;
    private readonly MainWindowViewModel viewModel;
    private ConnectionApprovalWindow? approvalWindow;
    private bool suppressAutoHide;

    public MainWindow(IClientAuditLog? auditLog = null)
    {
        InitializeComponent();

        var monitorService = new MonitorService();
        var coordinateMapper = new DisplayCoordinateMapper();
        var overlayService = new ReceiverOverlayService(monitorService, coordinateMapper);
        var targetRegionService = new TargetRegionService();
        var settings = ClientSettings.Load();
        var clientInstanceIdProvider = new ClientInstanceIdProvider();
        var protectedSessionStore = new ProtectedSessionStore(
            new DpapiDataProtector(),
            auditLog);
        var receiverRelayClient = new SignalRRelayClient(
            settings,
            clientInstanceIdProvider,
            expectedRole: RemotePointer.Contracts.Messages.ClientRole.Receiver,
            sessionStore: protectedSessionStore,
            auditLog: auditLog);
        var presenterRelayClient = new SignalRRelayClient(
            settings,
            clientInstanceIdProvider,
            expectedRole: RemotePointer.Contracts.Messages.ClientRole.Presenter,
            sessionStore: protectedSessionStore,
            auditLog: auditLog);
        viewModel = new MainWindowViewModel(
            monitorService,
            overlayService,
            targetRegionService,
            receiverRelayClient,
            presenterRelayClient,
            settings.Pointer.DefaultTtlMilliseconds,
            settings);
        DataContext = viewModel;

        trayIcon = new SystemTrayIcon(ShowFromTray, ExitFromTray);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.Presenter.PropertyChanged += OnViewModelPropertyChanged;
        StateChanged += OnWindowStateChanged;
        Loaded += OnLoaded;

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

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
        await viewModel.InitializeAsync();
        await viewModel.RestoreSessionsAsync();
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

                Show();
                WindowState = System.Windows.WindowState.Normal;
                PositionFlyout();
                Activate();
                Focus();
            });
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
            Hide();
        }
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

    private void ShowConnectionApprovalPrompt()
    {
        approvalWindow?.Close();
        approvalWindow = new ConnectionApprovalWindow(
            viewModel.PendingPresenterName,
            viewModel.ApprovePresenterCommand);
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
