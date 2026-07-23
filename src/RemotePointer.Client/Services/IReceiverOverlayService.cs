using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Services;

public interface IReceiverOverlayService : IDisposable
{
    event EventHandler<OverlayStateChangedEventArgs>? StateChanged;

    bool IsVisible { get; }

    void Show(MonitorDescriptor monitor);

    void Hide();

    bool ShowPointer(PointerEventMessage pointerEvent);
}
