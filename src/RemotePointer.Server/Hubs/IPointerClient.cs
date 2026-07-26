using RemotePointer.Contracts.Messages;

namespace RemotePointer.Server.Hubs;

public interface IPointerClient
{
    Task AnnotatorJoinRequested(AnnotatorDescriptor annotator);

    Task AnnotatorJoinCancelled(string annotatorConnectionId);

    Task SessionApproved(SessionStateMessage state);

    Task SessionCredentialIssued(SessionCredential credential);

    Task HostDisplayChanged(DisplayDescriptor display);

    Task PointerReceived(PointerEventMessage pointerEvent);

    Task PointerDisplayed(PointerAcknowledgement acknowledgement);

    Task AnnotationPaused(bool paused);

    Task SessionEnded(string reason);

    Task HostDirectoryChanged();
}
