using RemoteAnnotate.Client.Native;
using RemoteAnnotate.Contracts.Messages;

namespace RemoteAnnotate.Client.Services;

public sealed record MonitorDescriptor(
    nint Handle,
    DisplayDescriptor Display,
    PhysicalRectangle Bounds,
    PhysicalRectangle WorkArea,
    bool IsPrimary)
{
    public string SelectionLabel =>
        $"{Display.DisplayName} ({Display.WidthPixels} × {Display.HeightPixels})"
        + (IsPrimary ? " (Primary)" : string.Empty);
}
