namespace RemotePointer.Server.Sessions;

public interface ISessionSecretGenerator
{
    string GenerateIdentifier();

    string GenerateSecret();

    string HashSecret(string secret);

    bool SecretMatches(string secret, string expectedHash);
}
