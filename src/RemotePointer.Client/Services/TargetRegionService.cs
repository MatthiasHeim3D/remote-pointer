using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RemotePointer.Client.Overlays;
using RemotePointer.Contracts.Coordinates;

namespace RemotePointer.Client.Services;

public sealed class TargetRegionService : ITargetRegionService
{
    private readonly TargetRegionCalibrationStore calibrationStore;
    private RectangleD? calibratedRectangle;
    private string? calibrationIdentity;
    private bool disposed;
    private double expectedAspectRatio = 16d / 9d;
    private bool showExitHint = true;
    private TargetRegionWindow? window;

    public event EventHandler<TargetRegionStateChangedEventArgs>? StateChanged;

    public event EventHandler<PointerCapturedEventArgs>? PointerCaptured;

    public TargetRegionState State { get; private set; } = TargetRegionState.Inactive;

    public TargetRegionService(TargetRegionCalibrationStore? calibrationStore = null)
    {
        this.calibrationStore = calibrationStore ?? new TargetRegionCalibrationStore();
    }

    public void SetCalibrationIdentity(string? receiverIdentity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        calibrationIdentity = string.IsNullOrWhiteSpace(receiverIdentity) ? null : receiverIdentity;
        calibratedRectangle = calibrationIdentity is null
            ? null
            : calibrationStore.Load(calibrationIdentity);
    }

    public void SetShowExitHint(bool value) => showExitHint = value;

    public void BeginCalibration(double expectedAspectRatio)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(expectedAspectRatio) || expectedAspectRatio <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedAspectRatio));
        }

        this.expectedAspectRatio = expectedAspectRatio;
        CloseWindow();

        var defaultRectangle = CreateDefaultRectangle(expectedAspectRatio);
        var startingRectangle = calibratedRectangle ?? defaultRectangle;
        window = new TargetRegionWindow(
            startingRectangle,
            defaultRectangle,
            expectedAspectRatio,
            lockAspectRatio: true,
            showExitHint);
        window.CalibrationLocked += OnCalibrationLocked;
        window.CalibrationCancelled += OnCalibrationCancelled;
        window.Closed += OnWindowClosed;

        SetState(
            TargetRegionState.Calibrating,
            "Move and resize the target window, then start pointing.");
        window.Show();
        _ = window.Activate();
    }

    public void UpdateExpectedAspectRatio(double newExpectedAspectRatio)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(newExpectedAspectRatio) || newExpectedAspectRatio <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(newExpectedAspectRatio));
        }

        if (Math.Abs(expectedAspectRatio - newExpectedAspectRatio) < 0.000001d)
        {
            expectedAspectRatio = newExpectedAspectRatio;
            return;
        }

        expectedAspectRatio = newExpectedAspectRatio;
        InvalidateCalibration(
            "The receiver display shape changed. Recalibrate the target area.");
    }

    public void InvalidateCalibration(string message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        CloseWindow();
        SetState(TargetRegionState.Inactive, message);
    }

    public void TogglePointingMode()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (State == TargetRegionState.Pointing)
        {
            ExitPointingMode();
            return;
        }

        BeginCalibration(expectedAspectRatio);
    }

    private void EnterPointingMode()
    {
        if (calibratedRectangle is null)
        {
            return;
        }

        CloseWindow();
        var rectangle = calibratedRectangle.Value;
        window = new TargetRegionWindow(
            rectangle,
            rectangle,
            expectedAspectRatio,
            lockAspectRatio: false,
            showExitHint);
        window.EnterPointingMode();
        window.PointerCaptured += OnPointerCaptured;
        window.PointingExitRequested += OnPointingExitRequested;
        window.Closed += OnWindowClosed;

        SetState(
            TargetRegionState.Pointing,
            "Pointing active. Click, drag, Shift+drag, Shift+click, or right-drag; press Esc or Ctrl+Alt+P to stop.");
        var pointingWindow = window;
        pointingWindow.Show();

        // The command button's mouse event is still unwinding when Show returns.
        // Activating at dispatcher idle prevents that event from restoring focus to
        // the control window after pointing mode has started.
        _ = pointingWindow.Dispatcher.InvokeAsync(
            () =>
            {
                if (ReferenceEquals(window, pointingWindow) && pointingWindow.IsVisible)
                {
                    _ = pointingWindow.Activate();
                    _ = pointingWindow.Focus();
                    _ = Keyboard.Focus(pointingWindow);
                }
            },
            DispatcherPriority.ApplicationIdle);
    }

    public void ExitPointingMode()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (State != TargetRegionState.Pointing)
        {
            return;
        }

        CloseWindow();
        SetState(TargetRegionState.Ready, "Pointing stopped. The target region remains locked.");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        CloseWindow();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private static RectangleD CreateDefaultRectangle(double aspectRatio)
    {
        var workArea = SystemParameters.WorkArea;
        var width = Math.Min(720d, workArea.Width * 0.7d);
        var height = width / aspectRatio;
        if (height > workArea.Height * 0.7d)
        {
            height = workArea.Height * 0.7d;
            width = height * aspectRatio;
        }

        width = Math.Max(width, TargetRegionGeometry.MinimumWidth);
        height = Math.Max(height, TargetRegionGeometry.MinimumHeight);
        return new RectangleD(
            workArea.Left + ((workArea.Width - width) / 2d),
            workArea.Top + ((workArea.Height - height) / 2d),
            width,
            height);
    }

    private void OnCalibrationLocked(object? sender, CalibrationLockedEventArgs e)
    {
        calibratedRectangle = e.Rectangle;
        if (calibrationIdentity is not null)
        {
            calibrationStore.Save(calibrationIdentity, e.Rectangle);
        }

        CloseWindow();
        EnterPointingMode();
    }

    private void OnCalibrationCancelled(object? sender, EventArgs e)
    {
        CloseWindow();
        SetState(
            calibratedRectangle is null ? TargetRegionState.Inactive : TargetRegionState.Ready,
            calibratedRectangle is null
                ? "Calibration cancelled."
                : "Calibration cancelled; the previous target remains locked.");
    }

    private void OnPointerCaptured(object? sender, PointerCapturedEventArgs e) =>
        PointerCaptured?.Invoke(this, e);

    private void OnPointingExitRequested(object? sender, EventArgs e) => ExitPointingMode();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, window))
        {
            return;
        }

        DetachWindow();
        window = null;
        if (State is TargetRegionState.Calibrating or TargetRegionState.Pointing)
        {
            SetState(
                calibratedRectangle is null ? TargetRegionState.Inactive : TargetRegionState.Ready,
                "Target window closed.");
        }
    }

    private void CloseWindow()
    {
        if (window is null)
        {
            return;
        }

        var closingWindow = window;
        DetachWindow();
        window = null;
        closingWindow.Close();
    }

    private void DetachWindow()
    {
        if (window is null)
        {
            return;
        }

        window.CalibrationLocked -= OnCalibrationLocked;
        window.CalibrationCancelled -= OnCalibrationCancelled;
        window.PointerCaptured -= OnPointerCaptured;
        window.PointingExitRequested -= OnPointingExitRequested;
        window.Closed -= OnWindowClosed;
    }

    private void SetState(TargetRegionState state, string message, bool isError = false)
    {
        State = state;
        StateChanged?.Invoke(
            this,
            new TargetRegionStateChangedEventArgs(state, message, isError));
    }
}
