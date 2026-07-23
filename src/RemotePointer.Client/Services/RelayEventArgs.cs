using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Services;

public sealed class RelayConnectionStatusChangedEventArgs(
    RelayConnectionStatus status,
    string message) : EventArgs
{
    public RelayConnectionStatus Status { get; } = status;

    public string Message { get; } = message;
}

public sealed class PresenterJoinRequestedEventArgs(PresenterDescriptor presenter) : EventArgs
{
    public PresenterDescriptor Presenter { get; } = presenter;
}

public sealed class RelaySessionStateEventArgs(SessionStateMessage state) : EventArgs
{
    public SessionStateMessage State { get; } = state;
}

public sealed class RelayReceiverDisplayChangedEventArgs(DisplayDescriptor display) : EventArgs
{
    public DisplayDescriptor Display { get; } = display;
}

public sealed class RelayPointerEventArgs(PointerEventMessage pointerEvent) : EventArgs
{
    public PointerEventMessage PointerEvent { get; } = pointerEvent;
}

public sealed class RelayAcknowledgementEventArgs(PointerAcknowledgement acknowledgement) : EventArgs
{
    public PointerAcknowledgement Acknowledgement { get; } = acknowledgement;
}

public sealed class RelaySessionEndedEventArgs(string reason, bool expired) : EventArgs
{
    public string Reason { get; } = reason;

    public bool Expired { get; } = expired;
}
