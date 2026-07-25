namespace RemotePointer.Client.Services;

public interface ITargetRegionService : IDisposable
{
    event EventHandler<TargetRegionStateChangedEventArgs>? StateChanged;

    event EventHandler<PointerCapturedEventArgs>? PointerCaptured;

    event EventHandler? UsageHintsShown;

    TargetRegionState State { get; }

    void SetCalibrationIdentity(string? receiverIdentity);

    void SetUsageHintsState(bool showUsageHints, bool hasShownUsageHints);

    void SetDrawingOpacityPercent(int drawingOpacityPercent);

    void BeginCalibration(double expectedAspectRatio);

    void UpdateExpectedAspectRatio(double expectedAspectRatio);

    void InvalidateCalibration(string message);

    void TogglePointingMode();

    void ExitPointingMode();
}
