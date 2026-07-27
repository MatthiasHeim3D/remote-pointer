using Microsoft.Extensions.Options;
using RemotePointer.Contracts.Security;

namespace RemotePointer.Server.Security;

/// <summary>
/// Holds the key this relay expects every client to present. The password is derived once at
/// startup because the derivation is deliberately slow, and the plaintext is dropped as soon as
/// the key exists so it cannot be read back out of the relay later.
/// </summary>
public sealed class ServerPasswordVerifier
{
    private readonly string? expectedKey;

    public ServerPasswordVerifier(
        IOptions<ServerAccessOptions> options,
        ILogger<ServerPasswordVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var password = options.Value.ServerPassword;
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                AuditEventIds.RelayUnprotected,
                "No server password is configured. Every client that reaches this relay is "
                    + "admitted. Set Access:ServerPassword to protect it.");
            return;
        }

        // A password too short to be accepted by a client would lock everyone out, so it fails
        // the relay at startup rather than at the first connection attempt.
        expectedKey = ServerPasswordKey.Derive(password);
    }

    /// <summary>Whether this relay admits only clients that present the password.</summary>
    public bool IsRequired => expectedKey is not null;

    public bool Matches(string? presentedKey) =>
        expectedKey is null || ServerPasswordKey.Matches(presentedKey, expectedKey);
}
