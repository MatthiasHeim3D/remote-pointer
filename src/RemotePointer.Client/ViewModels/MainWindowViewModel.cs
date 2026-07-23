using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;

namespace RemotePointer.Client.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IMonitorService monitorService;
    private readonly IReceiverOverlayService overlayService;
    private double customNormalizedX = 0.5d;
    private double customNormalizedY = 0.5d;
    private bool disposed;
    private bool isError;
    private bool isOverlayVisible;
    private MonitorDescriptor? selectedMonitor;
    private string statusMessage = "Select a monitor to begin.";

    public MainWindowViewModel(
        IMonitorService monitorService,
        IReceiverOverlayService overlayService)
    {
        this.monitorService = monitorService ?? throw new ArgumentNullException(nameof(monitorService));
        this.overlayService = overlayService ?? throw new ArgumentNullException(nameof(overlayService));
        this.overlayService.StateChanged += OnOverlayStateChanged;

        RefreshMonitorsCommand = new RelayCommand(_ => RefreshMonitors());
        ShowOverlayCommand = new RelayCommand(_ => ShowOverlay());
        HideOverlayCommand = new RelayCommand(_ => overlayService.Hide());
        ShowPresetMarkerCommand = new RelayCommand(ShowPresetMarker);
        ShowCustomMarkerCommand = new RelayCommand(_ => ShowCustomMarker());

        RefreshMonitors();
    }

    public ObservableCollection<MonitorDescriptor> Monitors { get; } = [];

    public MonitorDescriptor? SelectedMonitor
    {
        get => selectedMonitor;
        set => SetProperty(ref selectedMonitor, value);
    }

    public double CustomNormalizedX
    {
        get => customNormalizedX;
        set => SetProperty(ref customNormalizedX, value);
    }

    public double CustomNormalizedY
    {
        get => customNormalizedY;
        set => SetProperty(ref customNormalizedY, value);
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

    public ICommand RefreshMonitorsCommand { get; }

    public ICommand ShowOverlayCommand { get; }

    public ICommand HideOverlayCommand { get; }

    public ICommand ShowPresetMarkerCommand { get; }

    public ICommand ShowCustomMarkerCommand { get; }

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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        overlayService.StateChanged -= OnOverlayStateChanged;
        overlayService.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private void ShowOverlay()
    {
        if (SelectedMonitor is null)
        {
            SetStatus("Select a connected monitor before showing the overlay.", isError: true);
            return;
        }

        try
        {
            overlayService.Show(SelectedMonitor);
        }
        catch (Win32Exception exception)
        {
            SetStatus($"The overlay could not be shown: {exception.Message}", isError: true);
        }
    }

    private void ShowPresetMarker(object? parameter)
    {
        var point = (parameter as string) switch
        {
            "TopLeft" => new NormalizedPoint(0d, 0d),
            "TopRight" => new NormalizedPoint(1d, 0d),
            "Center" => new NormalizedPoint(0.5d, 0.5d),
            "BottomLeft" => new NormalizedPoint(0d, 1d),
            "BottomRight" => new NormalizedPoint(1d, 1d),
            _ => throw new ArgumentException("Unknown marker preset.", nameof(parameter)),
        };

        _ = overlayService.ShowMarker(point);
    }

    private void ShowCustomMarker()
    {
        if (!double.IsFinite(CustomNormalizedX)
            || !double.IsFinite(CustomNormalizedY)
            || CustomNormalizedX is < 0d or > 1d
            || CustomNormalizedY is < 0d or > 1d)
        {
            SetStatus("Custom coordinates must be between 0.0 and 1.0.", isError: true);
            return;
        }

        _ = overlayService.ShowMarker(
            new NormalizedPoint(CustomNormalizedX, CustomNormalizedY));
    }

    private void OnOverlayStateChanged(object? sender, OverlayStateChangedEventArgs e)
    {
        IsOverlayVisible = e.IsVisible;
        SetStatus(e.Message, e.IsError);
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsError = isError;
    }
}
