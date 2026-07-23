using System.ComponentModel;
using System.Runtime.InteropServices;
using RemotePointer.Client.Native;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Services;

public sealed class MonitorService : IMonitorService
{
    private const double DefaultDpi = 96d;

    public IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var monitors = new List<MonitorDescriptor>();
        NativeMethods.MonitorEnumProcedure callback = (monitor, _, _, _) =>
        {
            monitors.Add(CreateDescriptor(monitor));
            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(0, 0, callback, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Monitor enumeration failed.");
        }

        return monitors
            .OrderByDescending(monitor => monitor.IsPrimary)
            .ThenBy(monitor => monitor.Bounds.Left)
            .ThenBy(monitor => monitor.Bounds.Top)
            .ToArray();
    }

    public MonitorDescriptor? FindByDisplayId(string displayId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayId);

        return GetMonitors().FirstOrDefault(
            monitor => string.Equals(
                monitor.Display.DisplayId,
                displayId,
                StringComparison.OrdinalIgnoreCase));
    }

    private static MonitorDescriptor CreateDescriptor(nint monitor)
    {
        var nativeInfo = new MonitorInfoEx
        {
            Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref nativeInfo))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Monitor information could not be read.");
        }

        var bounds = ToRectangle(nativeInfo.Monitor);
        var workArea = ToRectangle(nativeInfo.WorkArea);
        var dpi = GetEffectiveDpi(monitor);
        var deviceName = GetDeviceName(nativeInfo);
        var displayName = GetDisplayName(deviceName);
        var isPrimary = (nativeInfo.Flags & NativeMethods.MonitorInfoPrimary) != 0;

        var display = new DisplayDescriptor(
            deviceName,
            displayName,
            bounds.Width,
            bounds.Height,
            dpi / DefaultDpi,
            GetRotationDegrees(deviceName));

        return new MonitorDescriptor(monitor, display, bounds, workArea, isPrimary);
    }

    private static double GetEffectiveDpi(nint monitor)
    {
        var result = NativeMethods.GetDpiForMonitor(
            monitor,
            MonitorDpiType.Effective,
            out var dpiX,
            out _);

        return result == 0 && dpiX > 0 ? dpiX : DefaultDpi;
    }

    private static string GetDisplayName(string deviceName)
    {
        const string devicePrefix = @"\\.\DISPLAY";
        return deviceName.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)
            ? $"Display {deviceName[devicePrefix.Length..]}"
            : deviceName;
    }

    private static unsafe string GetDeviceName(MonitorInfoEx monitorInfo)
    {
        return new string(monitorInfo.DeviceName).TrimEnd('\0');
    }

    private static unsafe int GetRotationDegrees(string deviceName)
    {
        var deviceMode = new DeviceMode
        {
            Size = (ushort)sizeof(DeviceMode),
        };

        if (!NativeMethods.EnumDisplaySettings(
                deviceName,
                NativeMethods.EnumCurrentSettings,
                ref deviceMode))
        {
            return 0;
        }

        return deviceMode.Mode.Display.Orientation switch
        {
            1 => 90,
            2 => 180,
            3 => 270,
            _ => 0,
        };
    }

    private static PhysicalRectangle ToRectangle(NativeRectangle rectangle) => new(
        rectangle.Left,
        rectangle.Top,
        checked(rectangle.Right - rectangle.Left),
        checked(rectangle.Bottom - rectangle.Top));
}
