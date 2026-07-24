using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Runtime.InteropServices;
using RemotePointer.Client.Native;

namespace RemotePointer.Client.Configuration;

internal sealed class WindowsAccountProfileDefaultsProvider : IUserProfileDefaultsProvider
{
    private const int MaximumDirectoryPictureBytes = 100 * 1_024;
    private const int MaximumUserNameLength = 128;

    public UserProfileDefaults GetCurrentProfile()
    {
        var userName = TryGetWindowsDisplayName();
        byte[]? picture = null;

        try
        {
            using var principal = UserPrincipal.Current;
            if (principal is not null)
            {
                userName ??= NormalizeUserName(principal.DisplayName);
                if (principal.GetUnderlyingObject() is DirectoryEntry directoryEntry)
                {
                    using (directoryEntry)
                    {
                        picture = directoryEntry.Properties["thumbnailPhoto"].Value is byte[]
                            { Length: > 0 } bytes
                            && bytes.Length <= MaximumDirectoryPictureBytes
                            ? [.. bytes]
                            : null;
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is PrincipalException
                or InvalidOperationException
                or UnauthorizedAccessException
                or PlatformNotSupportedException
                or COMException)
        {
            // Directory information is an optional best-effort default.
        }

        return new UserProfileDefaults(userName, picture);
    }

    private static unsafe string? TryGetWindowsDisplayName()
    {
        uint characterCount = 0;
        _ = NativeMethods.GetUserNameEx(
            ExtendedNameFormat.Display,
            null,
            ref characterCount);
        if (characterCount is 0 or > MaximumUserNameLength + 1)
        {
            return null;
        }

        var buffer = new char[characterCount];
        fixed (char* bufferPointer = buffer)
        {
            if (NativeMethods.GetUserNameEx(
                    ExtendedNameFormat.Display,
                    bufferPointer,
                    ref characterCount) == 0)
            {
                return null;
            }
        }

        var terminatorIndex = Array.IndexOf(buffer, '\0');
        return NormalizeUserName(new string(
            buffer,
            0,
            terminatorIndex < 0 ? buffer.Length : terminatorIndex));
    }

    private static string? NormalizeUserName(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > MaximumUserNameLength
            ? null
            : normalized;
    }
}
