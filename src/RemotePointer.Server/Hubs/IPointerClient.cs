using RemotePointer.Contracts.Messages;

namespace RemotePointer.Server.Hubs;

public interface IPointerClient
{
    Task PresenterJoinRequested(PresenterDescriptor presenter);

    Task SessionApproved(SessionStateMessage state);

    Task SessionCredentialIssued(SessionCredential credential);

    Task ReceiverDisplayChanged(DisplayDescriptor display);

    Task PointerReceived(PointerEventMessage pointerEvent);

    Task PointerDisplayed(PointerAcknowledgement acknowledgement);

    Task SessionEnded(string reason);

    Task ReceiverDirectoryChanged();
}
