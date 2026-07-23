namespace RemotePointer.Client.Services;

public sealed class TargetRegionStateChangedEventArgs(
    TargetRegionState state,
    string message,
    bool isError = false) : EventArgs
{
    public TargetRegionState State { get; } = state;

    public string Message { get; } = message;

    public bool IsError { get; } = isError;
}
