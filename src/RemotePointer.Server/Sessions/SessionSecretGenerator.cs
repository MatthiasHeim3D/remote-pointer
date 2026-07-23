using System.Security.Cryptography;
using System.Text;
using RemotePointer.Contracts.Validation;

namespace RemotePointer.Server.Sessions;

public sealed class SessionSecretGenerator : ISessionSecretGenerator
{
    private const string PairingAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int SecretBytes = 32;

    public string GenerateIdentifier() => GenerateSecret();

    public string GeneratePairingCode()
    {
        Span<char> code = stackalloc char[PairingCodeValidator.CodeLength];
        for (var index = 0; index < code.Length; index++)
        {
            code[index] = PairingAlphabet[RandomNumberGenerator.GetInt32(PairingAlphabet.Length)];
        }

        return new string(code);
    }

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
