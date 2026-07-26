using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RemotePointer.Client.Native;

namespace RemotePointer.Client.Views;

/// <summary>
/// Bottom-right corner placement shared by the windows that open from the notification area.
/// </summary>
/// <remarks>
/// The work is done in physical pixels through <c>SetWindowPos</c> rather than through
/// <see cref="Window.Left"/> and <see cref="Window.Top"/>. Every device-independent route into
/// this is anchored to a DPI captured earlier: <see cref="SystemParameters.WorkArea"/> reports the
/// primary monitor against the DPI the process started with, and the DPI WPF holds for a window
/// that was hidden when the display scale changed has not been refreshed either. Changing the
/// Windows scale while the app runs therefore left the flyout off its corner. Physical pixels have
/// no such baseline, so nothing here goes stale.
/// </remarks>
internal static class FlyoutPlacement
{
    private const double EdgeMargin = 12d;
    private const double DefaultDpi = 96d;

    /// <summary>
    /// Places <paramref name="window"/> against the bottom-right corner of the work area of the
    /// monitor it currently sits on.
    /// </summary>
    internal static void PlaceInBottomCorner(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero
            || !TryGetMonitorWorkArea(handle, out var workArea, out var dpi)
            || !NativeMethods.GetWindowRect(handle, out var windowRectangle))
        {
            PlaceUsingLogicalUnits(window);
            return;
        }

        var placement = CalculateBottomCorner(workArea, windowRectangle, dpi);
        if (!NativeMethods.SetWindowPos(
                handle,
                nint.Zero,
                placement.X,
                placement.Y,
                0,
                0,
                NativeMethods.SwpNoSize
                    | NativeMethods.SwpNoZOrder
                    | NativeMethods.SwpNoActivate))
        {
            PlaceUsingLogicalUnits(window);
        }
    }

    /// <summary>
    /// Top-left physical position that seats a window of <paramref name="windowRectangle"/>'s size
    /// in the bottom-right corner of <paramref name="workArea"/>, inset by a margin scaled to
    /// <paramref name="dpi"/> so the gap looks the same at every display scale.
    /// </summary>
    internal static (int X, int Y) CalculateBottomCorner(
        NativeRectangle workArea,
        NativeRectangle windowRectangle,
        double dpi)
    {
        var margin = (int)Math.Round(
            EdgeMargin * (dpi > 0d ? dpi : DefaultDpi) / DefaultDpi,
            MidpointRounding.AwayFromZero);
        var width = windowRectangle.Right - windowRectangle.Left;
        var height = windowRectangle.Bottom - windowRectangle.Top;

        return (workArea.Right - width - margin, workArea.Bottom - height - margin);
    }

    private static bool TryGetMonitorWorkArea(
        nint handle,
        out NativeRectangle workArea,
        out double dpi)
    {
        workArea = default;
        dpi = DefaultDpi;

        var monitor = NativeMethods.MonitorFromWindow(
            handle,
            NativeMethods.MonitorDefaultToNearest);
        if (monitor == nint.Zero)
        {
            return false;
        }

        var info = new MonitorInfoEx
        {
            Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        workArea = info.WorkArea;

        // Asked of the monitor rather than of the window, so a scale change that happened while
        // this window was hidden is still reflected.
        if (NativeMethods.GetDpiForMonitor(monitor, MonitorDpiType.Effective, out var dpiX, out _) == 0
            && dpiX > 0)
        {
            dpi = dpiX;
        }

        return true;
    }

    /// <summary>
    /// Fallback for the moments before a window has a handle, and for the rare case where the
    /// native calls fail. Carries the stale-DPI weakness described on the class.
    /// </summary>
    private static void PlaceUsingLogicalUnits(Window window)
    {
        var workArea = SystemParameters.WorkArea;
        window.Left = workArea.Right - window.Width - EdgeMargin;
        window.Top = workArea.Bottom - window.Height - EdgeMargin;
    }
}
