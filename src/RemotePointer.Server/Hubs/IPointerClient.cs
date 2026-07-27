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

    /// <summary>
    /// Tells an annotator the colour it should now draw in. Sent whenever allocation moves it,
    /// which includes being moved back onto its own preference once the clash clears.
    /// </summary>
    Task AnnotationColorAssigned(string color);

    Task SessionEnded(string reason);

    Task HostDirectoryChanged();
}
