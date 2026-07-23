using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemotePointer.Client.Native;

public sealed class GlobalHotKeyRegistration : IDisposable
{
    public const int TogglePointerHotKeyId = 0x5250;

    private readonly nint windowHandle;
    private bool registered;

    public GlobalHotKeyRegistration(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        this.windowHandle = windowHandle;
        var modifiers = (uint)(NativeMethods.ModifierControl
            | NativeMethods.ModifierAlt
            | NativeMethods.ModifierNoRepeat);

        if (!NativeMethods.RegisterHotKey(
                windowHandle,
                TogglePointerHotKeyId,
                modifiers,
                virtualKey: 0x50))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Ctrl+Alt+P could not be registered as the global pointing hotkey.");
        }

        registered = true;
    }

    public void Dispose()
    {
        if (!registered)
        {
            return;
        }

        _ = NativeMethods.UnregisterHotKey(windowHandle, TogglePointerHotKeyId);
        registered = false;
        GC.SuppressFinalize(this);
    }
}
