using System.Runtime.InteropServices;

namespace RemotePointer.Client.Native;

internal static partial class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const int EnumCurrentSettings = -1;
    internal const int HtTransparent = -1;
    internal const int MaNoActivate = 3;
    internal const int ModifierAlt = 0x0001;
    internal const int ModifierControl = 0x0002;
    internal const int ModifierNoRepeat = 0x4000;
    internal const int MonitorInfoPrimary = 0x00000001;
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const int SwpNoActivate = 0x0010;
    internal const int SwpShowWindow = 0x0040;
    internal const int WmDisplayChange = 0x007E;
    internal const int WmMouseActivate = 0x0021;
    internal const int WmNcHitTest = 0x0084;
    internal const int WmHotKey = 0x0312;
    internal const long WsExLayered = 0x00080000L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExTransparent = 0x00000020L;

    internal static readonly nint HwndTopmost = new(-1);

    internal delegate bool MonitorEnumProcedure(
        nint monitor,
        nint deviceContext,
        nint monitorRectangle,
        nint userData);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumProcedure callback,
        nint userData);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(nint monitor, ref MonitorInfoEx monitorInfo);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(
        nint monitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    [LibraryImport(
        "user32.dll",
        EntryPoint = "EnumDisplaySettingsW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplaySettings(
        string deviceName,
        int modeNumber,
        ref DeviceMode deviceMode);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static partial nint SetWindowLongPtr(nint window, int index, nint newValue);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(
        nint window,
        int hotKeyId,
        uint modifiers,
        uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint window, int hotKeyId);
}

internal enum MonitorDpiType
{
    Effective = 0,
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRectangle
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct MonitorInfoEx
{
    internal uint Size;
    internal NativeRectangle Monitor;
    internal NativeRectangle WorkArea;
    internal uint Flags;

    internal fixed char DeviceName[32];
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct DeviceMode
{
    internal fixed char DeviceName[32];
    internal ushort SpecVersion;
    internal ushort DriverVersion;
    internal ushort Size;
    internal ushort DriverExtra;
    internal uint Fields;
    internal DeviceModeUnion Mode;
    internal short Color;
    internal short Duplex;
    internal short YResolution;
    internal short TrueTypeOption;
    internal short Collate;
    internal fixed char FormName[32];
    internal ushort LogPixels;
    internal uint BitsPerPixel;
    internal uint PixelsWidth;
    internal uint PixelsHeight;
    internal uint DisplayFlags;
    internal uint DisplayFrequency;
    internal uint IcmMethod;
    internal uint IcmIntent;
    internal uint MediaType;
    internal uint DitherType;
    internal uint Reserved1;
    internal uint Reserved2;
    internal uint PanningWidth;
    internal uint PanningHeight;
}

[StructLayout(LayoutKind.Explicit)]
internal struct DeviceModeUnion
{
    [FieldOffset(0)]
    internal DisplayModeFields Display;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayModeFields
{
    internal NativePoint Position;
    internal uint Orientation;
    internal uint FixedOutput;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    internal int X;
    internal int Y;
}
