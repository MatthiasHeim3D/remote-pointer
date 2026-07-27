using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RemoteAnnotate.Client.Configuration;
using RemoteAnnotate.Client.Overlays;
using RemoteAnnotate.Contracts.Coordinates;
using RemoteAnnotate.Contracts.Messages;

namespace RemoteAnnotate.Client.Services;

public sealed class TargetRegionService : ITargetRegionService
{
    private readonly TargetRegionCalibrationStore calibrationStore;
    private RectangleD? calibratedRectangle;
    private string? calibrationIdentity;
    private bool disposed;
    private bool isAnnotationPaused;
    private double expectedAspectRatio = 16d / 9d;
    private bool showUsageHints = true;
    private bool hasShownUsageHints;
    private int drawingOpacityPercent = PointerSettings.DefaultDrawingOpacityPercent;
    private string annotationColor = AnnotationColors.Default;
    private TargetRegionWindow? window;

    public event EventHandler<TargetRegionStateChangedEventArgs>? StateChanged;

    public event EventHandler<PointerCapturedEventArgs>? PointerCaptured;

    public event EventHandler? UsageHintsShown;

    public TargetRegionState State { get; private set; } = TargetRegionState.Inactive;

    public TargetRegionService(TargetRegionCalibrationStore? calibrationStore = null)
    {
        this.calibrationStore = calibrationStore ?? new TargetRegionCalibrationStore();
    }

    public void SetCalibrationIdentity(string? hostIdentity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        calibrationIdentity = string.IsNullOrWhiteSpace(hostIdentity) ? null : hostIdentity;
        calibratedRectangle = calibrationIdentity is null
            ? null
            : calibrationStore.Load(calibrationIdentity);
    }

    public void SetUsageHintsState(bool showUsageHints, bool hasShownUsageHints)
    {
        this.showUsageHints = showUsageHints;
        this.hasShownUsageHints = hasShownUsageHints;
    }

    public void SetDrawingOpacityPercent(int drawingOpacityPercent) =>
        this.drawingOpacityPercent =
            PointerSettings.ClampDrawingOpacityPercent(drawingOpacityPercent);

    public void SetAnnotationColor(string? annotationColor)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        this.annotationColor = AnnotationColors.Normalize(annotationColor);
        // Pushed into an open window rather than only remembered for the next one: the colour is
        // usually changed mid-session, and reaching it should not cost a recalibration.
        window?.SetAnnotationColor(this.annotationColor);
    }

    public void SetAnnotationPaused(bool paused)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        isAnnotationPaused = paused;
        window?.SetAnnotationPaused(paused);
    }

    private double DrawingOpacity => drawingOpacityPercent / 100d;

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
            showUsageHints,
            drawingOpacity: DrawingOpacity,
            annotationColor: annotationColor);
        window.CalibrationLocked += OnCalibrationLocked;
        window.CalibrationCancelled += OnCalibrationCancelled;
        window.Closed += OnWindowClosed;

        SetState(
            TargetRegionState.Calibrating,
            "Move and resize the target window, then start annotating.");
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
            "The host display shape changed. Recalibrate the target area.");
    }

    public void InvalidateCalibration(string message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        CloseWindow();
        SetState(TargetRegionState.Inactive, message);
    }

    public void ToggleAnnotatingMode()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (State == TargetRegionState.Annotating)
        {
            ExitAnnotatingMode();
            return;
        }

        BeginCalibration(expectedAspectRatio);
    }

    private void EnterAnnotatingMode()
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
            showUsageHints,
            expandUsageHintsInitially: !hasShownUsageHints,
            drawingOpacity: DrawingOpacity,
            annotationColor: annotationColor);
        window.EnterAnnotatingMode();
        window.SetAnnotationPaused(isAnnotationPaused);
        if (!hasShownUsageHints)
        {
            hasShownUsageHints = true;
            UsageHintsShown?.Invoke(this, EventArgs.Empty);
        }
        window.PointerCaptured += OnPointerCaptured;
        window.AnnotatingExitRequested += OnAnnotatingExitRequested;
        window.Closed += OnWindowClosed;

        SetState(
            TargetRegionState.Annotating,
            "Annotating active. Click, drag, Shift+drag, Shift+click, right-drag, or Shift+right-drag; press Esc or Ctrl+Alt+P to stop.");
        var annotatingWindow = window;
        annotatingWindow.Show();

        // The command button's mouse event is still unwinding when Show returns.
        // Activating at dispatcher idle prevents that event from restoring focus to
        // the control window after annotating mode has started.
        _ = annotatingWindow.Dispatcher.InvokeAsync(
            () =>
            {
                if (ReferenceEquals(window, annotatingWindow) && annotatingWindow.IsVisible)
                {
                    _ = annotatingWindow.Activate();
                    _ = annotatingWindow.Focus();
                    _ = Keyboard.Focus(annotatingWindow);
                }
            },
            DispatcherPriority.ApplicationIdle);
    }

    public void ExitAnnotatingMode()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (State != TargetRegionState.Annotating)
        {
            return;
        }

        CloseWindow();
        SetState(TargetRegionState.Ready, "Annotating stopped. The target region remains locked.");
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
        EnterAnnotatingMode();
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

    private void OnAnnotatingExitRequested(object? sender, EventArgs e) => ExitAnnotatingMode();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, window))
        {
            return;
        }

        DetachWindow();
        window = null;
        if (State is TargetRegionState.Calibrating or TargetRegionState.Annotating)
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
        window.AnnotatingExitRequested -= OnAnnotatingExitRequested;
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
