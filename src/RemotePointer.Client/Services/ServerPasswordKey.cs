using System.Security.Cryptography;
using System.Text;

namespace RemotePointer.Client.Services;

/// <summary>
/// Turns a server password into the group key the relay sees. The password itself never leaves
/// the machine: two clients reach the same group only by deriving the same key, and the relay
/// stores and compares that key without being able to recover the password from it.
/// </summary>
public static class ServerPasswordKey
{
    public const int MinimumPasswordLength = 8;

    /// <summary>
    /// Fixed because every client has to derive the same key from the same password for the
    /// group to exist at all. Iterations make a leaked key expensive to attack offline.
    /// </summary>
    private static readonly byte[] Salt =
        Encoding.UTF8.GetBytes("RemotePointer.ServerPassword.v1");
    private const int Iterations = 210_000;
    private const int KeyBytes = 32;

    /// <summary>
    /// Separates the shown code from the key it is derived from, so the code is not a piece of
    /// the key that the settings screen puts on display.
    /// </summary>
    private const string CheckCodePrefix = "RemotePointer.ServerPasswordCheck.v1:";
    private const int CheckCodeBytes = 4;

    public static bool IsValidPassword(string? password) =>
        password is not null && password.Trim().Length >= MinimumPasswordLength;

    public static string Derive(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var trimmed = password.Trim();
        if (trimmed.Length < MinimumPasswordLength)
        {
            throw new ArgumentException(
                $"The server password must be at least {MinimumPasswordLength} characters.",
                nameof(password));
        }

        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(trimmed),
            Salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
        try
        {
            return Convert.ToBase64String(key)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// A short code that identifies which password a client is using without disclosing it.
    /// Two clients show the same code only if they derived the same key, which is what makes
    /// it worth comparing across machines. It is safe to display: recovering the password from
    /// it still means guessing passwords through <see cref="Derive"/>, and the code is short
    /// enough that many passwords produce the same one.
    /// </summary>
    public static string? DeriveCheckCode(string? groupKey)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return null;
        }

        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(CheckCodePrefix + groupKey));
        var code = Convert.ToHexString(digest.AsSpan(0, CheckCodeBytes));
        return $"{code[..4]}-{code[4..]}";
    }
}
