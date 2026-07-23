using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.ComponentModel;
using RemotePointer.Client.Native;
using RemotePointer.Client.Services;
using RemotePointer.Client.ViewModels;

namespace RemotePointer.Client.Views;

public partial class MainWindow : Window
{
    private GlobalHotKeyRegistration? hotKeyRegistration;
    private HwndSource? source;
    private readonly MainWindowViewModel viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var monitorService = new MonitorService();
        var coordinateMapper = new DisplayCoordinateMapper();
        var overlayService = new ReceiverOverlayService(monitorService, coordinateMapper);
        var targetRegionService = new TargetRegionService();
        viewModel = new MainWindowViewModel(
            monitorService,
            overlayService,
            targetRegionService);
        DataContext = viewModel;

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
        source?.RemoveHook(WindowMessageHook);
        source = null;
        hotKeyRegistration?.Dispose();
        hotKeyRegistration = null;
        viewModel.Dispose();
    }
}
