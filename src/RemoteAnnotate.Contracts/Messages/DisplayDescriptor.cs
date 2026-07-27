namespace RemoteAnnotate.Contracts.Messages;

public sealed record DisplayDescriptor(
    string DisplayId,
    string DisplayName,
    int WidthPixels,
    int HeightPixels,
    double ScaleFactor,
    int RotationDegrees)
{
    public double AspectRatio => (double)WidthPixels / HeightPixels;
}
