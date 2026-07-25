using System.Security.Cryptography;
using System.Text;

namespace RemotePointer.Server.Sessions;

public sealed class SessionSecretGenerator : ISessionSecretGenerator
{
    private const int SecretBytes = 32;

    public string GenerateIdentifier() => GenerateSecret();

    public string GenerateSecret()
    {
        Span<byte> bytes = stackalloc byte[SecretBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public string HashSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hash);
    }

    public bool SecretMatches(string secret, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedBytes);
    }
}
