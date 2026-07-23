using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.ComponentModel;
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

    public MainWindow()
    {
        InitializeComponent();

        var monitorService = new MonitorService();
        var coordinateMapper = new DisplayCoordinateMapper();
        var overlayService = new ReceiverOverlayService(monitorService, coordinateMapper);
        var targetRegionService = new TargetRegionService();
        var settings = ClientSettings.Load();
        var clientInstanceIdProvider = new ClientInstanceIdProvider();
        var receiverRelayClient = new SignalRRelayClient(settings, clientInstanceIdProvider);
        var presenterRelayClient = new SignalRRelayClient(settings, clientInstanceIdProvider);
        viewModel = new MainWindowViewModel(
            monitorService,
            overlayService,
            targetRegionService,
            receiverRelayClient,
            presenterRelayClient,
            settings.Pointer.DefaultTtlMilliseconds);
        DataContext = viewModel;

        trayIcon = new SystemTrayIcon(ShowFromTray, ExitFromTray);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.Presenter.PropertyChanged += OnViewModelPropertyChanged;
        StateChanged += OnWindowStateChanged;

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
            _ = Dispatcher.InvokeAsync(viewModel.RefreshMonitors, DispatcherPriority.Background);
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
        StateChanged -= OnWindowStateChanged;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.Presenter.PropertyChanged -= OnViewModelPropertyChanged;
        source?.RemoveHook(WindowMessageHook);
        source = null;
        hotKeyRegistration?.Dispose();
        hotKeyRegistration = null;
        trayIcon.Dispose();
        viewModel.Dispose();
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
        _ = sender;
        _ = e;
        var status = viewModel.Presenter.IsPointing
            ? "Pointing active"
            : viewModel.Presenter.IsSessionApproved
                ? "Presenter connected"
                : viewModel.HasReceiverSession
                    ? "Receiving session active"
                    : "Inactive";
        trayIcon.SetStatus(status);
    }

    private void ShowFromTray()
    {
        _ = Dispatcher.InvokeAsync(
            () =>
            {
                ShowInTaskbar = true;
                Show();
                WindowState = System.Windows.WindowState.Normal;
                Activate();
            });
    }

    private void ExitFromTray()
    {
        _ = Dispatcher.InvokeAsync(Close);
    }
}
