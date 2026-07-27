using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace RemoteAnnotate.Server.Security;

/// <summary>
/// The relay's front door. A client presents the key derived from the server password as a
/// bearer token on the connection itself, so a client that does not hold the password is turned
/// away at negotiate and never reaches the hub. The password is never sent — only the derived
/// key — and it travels in a header rather than the query string to keep it out of proxy logs.
/// </summary>
public sealed class ServerPasswordAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ServerPasswordVerifier verifier)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "ServerPassword";

    private const string BearerPrefix = "Bearer ";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!verifier.IsRequired)
        {
            return Task.FromResult(Admit());
        }

        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return Task.FromResult(Reject("missing"));
        }

        return Task.FromResult(
            verifier.Matches(header[BearerPrefix.Length..])
                ? Admit()
                : Reject("mismatch"));
    }

    private AuthenticateResult Admit()
    {
        // The relay has no notion of who a client is — the password admits it, and identity
        // inside a session comes from the session credentials. The principal exists only so the
        // authorization policy sees an authenticated request.
        var identity = new ClaimsIdentity(SchemeName);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private AuthenticateResult Reject(string reason)
    {
        // The reason distinguishes a client that never had the password from one whose password
        // is wrong. Neither the presented key nor any part of it is logged.
        Logger.LogWarning(
            AuditEventIds.ServerPasswordRejected,
            "Rejected a client that did not present the server password. Reason={Reason} Path={Path}",
            reason,
            Request.Path);
        return AuthenticateResult.Fail("The server password is not correct.");
    }
}
