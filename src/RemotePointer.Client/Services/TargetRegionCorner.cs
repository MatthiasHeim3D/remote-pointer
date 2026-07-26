namespace RemotePointer.Client.Services;

/// <summary>
/// Identifies which corner of the calibration window a resize drag started from. The corner
/// diagonally opposite the dragged one stays anchored while the window is resized.
/// </summary>
public enum TargetRegionCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}
