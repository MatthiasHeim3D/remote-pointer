namespace RemotePointer.Client.Services;

public interface ITargetRegionService : IDisposable
{
    event EventHandler<TargetRegionStateChangedEventArgs>? StateChanged;

    event EventHandler<PointerCapturedEventArgs>? PointerCaptured;

    event EventHandler? UsageHintsShown;

    TargetRegionState State { get; }

    void SetCalibrationIdentity(string? hostIdentity);

    void SetUsageHintsState(bool showUsageHints, bool hasShownUsageHints);

    void SetDrawingOpacityPercent(int drawingOpacityPercent);

    /// <summary>
    /// Marks the target region as paused by the host: it stops capturing input and says so, but
    /// stays open and calibrated so annotating resumes the moment the host lifts the pause.
    /// </summary>
    void SetAnnotationPaused(bool paused);

    void BeginCalibration(double expectedAspectRatio);

    void UpdateExpectedAspectRatio(double expectedAspectRatio);

    void InvalidateCalibration(string message);

    void ToggleAnnotatingMode();

    void ExitAnnotatingMode();
}
