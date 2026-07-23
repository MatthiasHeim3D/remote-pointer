namespace RemotePointer.Client.Services;

public sealed class OverlayStateChangedEventArgs(
    string message,
    bool isError,
    bool isVisible) : EventArgs
{
    public string Message { get; } = message;

    public bool IsError { get; } = isError;

    public bool IsVisible { get; } = isVisible;
}
