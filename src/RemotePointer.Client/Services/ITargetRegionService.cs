namespace RemotePointer.Client.Services;

public interface ITargetRegionService : IDisposable
{
    event EventHandler<TargetRegionStateChangedEventArgs>? StateChanged;

    event EventHandler<PointerCapturedEventArgs>? PointerCaptured;

    TargetRegionState State { get; }

    void BeginCalibration(double expectedAspectRatio);

    void UpdateExpectedAspectRatio(double expectedAspectRatio);

    void InvalidateCalibration(string message);

    void TogglePointingMode();

    void ExitPointingMode();
}
