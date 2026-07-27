using System.Security.Cryptography;
using System.Text;

namespace RemoteAnnotate.Contracts.Security;

/// <summary>
/// Turns the relay's server password into the key a client presents to get in. The password
/// itself never leaves the machine that typed it: the client derives this key and sends only
/// the key, and the relay compares it against the key it derived from its own configured
/// password. Both sides run the same derivation, which is why it lives in the shared library.
/// </summary>
public static class ServerPasswordKey
{
    public const int MinimumPasswordLength = 8;

    /// <summary>
    /// Fixed because client and relay have to derive the same key from the same password for a
    /// client to be admitted at all. Iterations make a leaked key expensive to attack offline.
    /// </summary>
    private static readonly byte[] Salt =
        Encoding.UTF8.GetBytes("RemoteAnnotate.ServerPassword.v1");
    private const int Iterations = 210_000;
    private const int KeyBytes = 32;

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
    /// Compares a presented key against the expected one without leaking, through timing, how
    /// much of a wrong key was right.
    /// </summary>
    public static bool Matches(string? presentedKey, string expectedKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedKey);
        if (string.IsNullOrEmpty(presentedKey))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presentedKey),
            Encoding.UTF8.GetBytes(expectedKey));
    }
}
