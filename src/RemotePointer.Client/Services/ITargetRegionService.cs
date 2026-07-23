namespace RemotePointer.Client.Services;

public interface ITargetRegionService : IDisposable
{
    event EventHandler<TargetRegionStateChangedEventArgs>? StateChanged;

    event EventHandler<PointerCapturedEventArgs>? PointerCaptured;

    TargetRegionState State { get; }

    void BeginCalibration(double expectedAspectRatio, bool lockAspectRatio);

    void TogglePointingMode();

    void ExitPointingMode();
}
