using RemoteAnnotate.Contracts.Messages;

namespace RemoteAnnotate.Client.Services;

public interface IHostOverlayService : IDisposable
{
    event EventHandler<OverlayStateChangedEventArgs>? StateChanged;

    bool IsVisible { get; }

    void Show(MonitorDescriptor monitor);

    void Hide();

    bool ShowPointer(PointerEventMessage pointerEvent);
}
