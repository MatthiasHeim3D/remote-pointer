using RemoteAnnotate.Contracts.Coordinates;

namespace RemoteAnnotate.Client.Services;

public interface IDisplayCoordinateMapper
{
    double PhysicalPixelsToDips(double pixels, double scaleFactor);

    double DipsToPhysicalPixels(double dips, double scaleFactor);

    PointD ToOverlayPoint(NormalizedPoint point, double overlayWidth, double overlayHeight);
}
