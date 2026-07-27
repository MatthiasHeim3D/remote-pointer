using RemotePointer.Client.Configuration;
using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Tests.Fakes;

internal sealed class FakeTargetRegionService : ITargetRegionService
{
    public event EventHandler<TargetRegionStateChangedEventArgs>? StateChanged;

    public event EventHandler<PointerCapturedEventArgs>? PointerCaptured;

    public event EventHandler? UsageHintsShown;

    public TargetRegionState State { get; private set; }

    public int BeginCalibrationCount { get; private set; }

    public double RequestedAspectRatio { get; private set; }

    public double UpdatedAspectRatio { get; private set; }

    public int InvalidateCount { get; private set; }

    public int ToggleCount { get; private set; }

    public int ExitCount { get; private set; }

    public string? CalibrationIdentity { get; private set; }

    public bool ShowUsageHints { get; private set; } = true;

    public bool HasShownUsageHints { get; private set; }

    public int DrawingOpacityPercent { get; private set; } =
        PointerSettings.DefaultDrawingOpacityPercent;

    public void SetCalibrationIdentity(string? hostIdentity) =>
        CalibrationIdentity = hostIdentity;

    public void SetUsageHintsState(bool showUsageHints, bool hasShownUsageHints)
    {
        ShowUsageHints = showUsageHints;
        HasShownUsageHints = hasShownUsageHints;
    }

    public void SetDrawingOpacityPercent(int drawingOpacityPercent) =>
        DrawingOpacityPercent = drawingOpacityPercent;

    public string AnnotationColor { get; private set; } = AnnotationColors.Default;

    public void SetAnnotationColor(string? annotationColor) =>
        AnnotationColor = annotationColor ?? AnnotationColors.Default;

    public bool IsAnnotationPaused { get; private set; }

    public void SetAnnotationPaused(bool paused) => IsAnnotationPaused = paused;

    public void BeginCalibration(double expectedAspectRatio)
    {
        BeginCalibrationCount++;
        RequestedAspectRatio = expectedAspectRatio;
    }

    public void UpdateExpectedAspectRatio(double expectedAspectRatio) =>
        UpdatedAspectRatio = expectedAspectRatio;

    public void InvalidateCalibration(string message)
    {
        _ = message;
        InvalidateCount++;
    }

    public void ToggleAnnotatingMode() => ToggleCount++;

    public void ExitAnnotatingMode()
    {
        ExitCount++;
    }

    public void RaiseState(TargetRegionState state, string message, bool isError = false)
    {
        State = state;
        StateChanged?.Invoke(
            this,
            new TargetRegionStateChangedEventArgs(state, message, isError));
    }

    public void RaiseUsageHintsShown() => UsageHintsShown?.Invoke(this, EventArgs.Empty);

    public void RaisePointer(
        NormalizedPoint point,
        PointerKind kind = PointerKind.Click,
        Guid? gestureId = null,
        string? text = null,
        NormalizedPoint[]? pathPoints = null) =>
        PointerCaptured?.Invoke(
            this,
            new PointerCapturedEventArgs(point, kind, gestureId, text, pathPoints));

    public void Dispose()
    {
    }
}
