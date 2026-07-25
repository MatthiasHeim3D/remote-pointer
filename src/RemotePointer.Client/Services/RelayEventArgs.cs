using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Services;

public sealed class RelayConnectionStatusChangedEventArgs(
    RelayConnectionStatus status,
    string message) : EventArgs
{
    public RelayConnectionStatus Status { get; } = status;

    public string Message { get; } = message;
}

public sealed class AnnotatorJoinRequestedEventArgs(AnnotatorDescriptor annotator) : EventArgs
{
    public AnnotatorDescriptor Annotator { get; } = annotator;
}

public sealed class AnnotatorJoinCancelledEventArgs(string annotatorConnectionId) : EventArgs
{
    public string AnnotatorConnectionId { get; } = annotatorConnectionId;
}

public sealed class RelaySessionStateEventArgs(SessionStateMessage state) : EventArgs
{
    public SessionStateMessage State { get; } = state;
}

public sealed class RelayHostDisplayChangedEventArgs(DisplayDescriptor display) : EventArgs
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
