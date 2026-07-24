namespace RemotePointer.Client.Services;

public interface ITargetRegionService : IDisposable
{
    event EventHandler<TargetRegionStateChangedEventArgs>? StateChanged;

    event EventHandler<PointerCapturedEventArgs>? PointerCaptured;

    TargetRegionState State { get; }

    void SetCalibrationIdentity(string? receiverIdentity);

    void SetShowExitHint(bool showExitHint);

    void BeginCalibration(double expectedAspectRatio);

    void UpdateExpectedAspectRatio(double expectedAspectRatio);

    void InvalidateCalibration(string message);

    void TogglePointingMode();

    void ExitPointingMode();
}
