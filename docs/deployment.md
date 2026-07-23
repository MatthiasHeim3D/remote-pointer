# Deployment

Phase 6 provides hardened publish and signing configuration. Phase 7 still owns the signed MSI/MSIX, upgrade/uninstall behavior, and corporate deployment runbook.

## Planned client deployment

- Self-contained Windows x64 output targeting `net10.0-windows`; Authenticode signing configuration is implemented.
- Per-Monitor V2 application manifest (implemented in Phase 2).
- MSI or MSIX suitable for silent Intune/SCCM deployment.
- Standard-user operation after installation.
- Machine-wide server URL configuration without storing session secrets in JSON.

## Planned server deployment

- Framework-dependent or containerized ASP.NET Core .NET 10 application.
- Organization-issued TLS certificate and approved interface binding.
- Inbound TCP 443 at the server; clients require outbound HTTPS only.
- JSON configuration with environment-variable overrides.
- Health endpoint at `/health`.

## Local Phase 5 workflow

```powershell
dotnet run --project src\RemotePointer.Server --launch-profile https
dotnet run --project src\RemotePointer.Client
dotnet run --project src\RemotePointer.Client
```

- HTTPS: `https://localhost:7243`
- Development HTTP: `http://localhost:5243`
- SignalR: `/hubs/pointer`
- Health: `/health`

Session and rate settings live in `appsettings.json`. Standard ASP.NET Core environment variables override them, for example `Sessions__MaximumSessionHours` and `RateLimits__EventsPerSecond`.

Client settings live in `src/RemotePointer.Client/appsettings.json` and are copied beside the executable. `REMOTEPOINTER_SERVER_BASEURL` overrides `Server:BaseUrl`. The URL must use HTTPS, including local development. The default HTTPS development certificate must be trusted for `https://localhost:7243`; certificate validation is never bypassed by the client.

## Production relay TLS

`appsettings.Production.json` defines an HTTPS-only Kestrel endpoint on TCP 443. Supply the organization certificate outside source control, for example with environment-backed configuration:

```text
ASPNETCORE_ENVIRONMENT=Production
Kestrel__Endpoints__Https__Certificate__Path=C:\ProgramData\RemotePointer\tls\relay.pfx
Kestrel__Endpoints__Https__Certificate__Password=<secret injected by deployment system>
```

The relay fails to start if no usable certificate is available. Production plaintext requests are rejected with status 400 rather than redirected. Bind interfaces, certificate ACLs, service identity, and secret injection must follow organization policy.

## Signed client publish

Install the Windows SDK signing tools and place the organization code-signing certificate in an accessible Windows certificate store. Publish with:

```powershell
dotnet publish src\RemotePointer.Client `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:EnableCodeSigning=true `
  -p:CodeSigningCertificateThumbprint=<certificate-thumbprint>
```

Optional MSBuild properties are `SignToolPath`, `CodeSigningTimestampUrl`, and `CodeSigningAdditionalArguments`. Signing uses SHA-256 and an RFC 3161 timestamp. Verify the result before packaging:

```powershell
Get-AuthenticodeSignature .\src\RemotePointer.Client\bin\Release\net10.0-windows\win-x64\publish\RemotePointer.Client.exe
```

Installer signing, package upgrade/uninstall behavior, firewall details, and the operational runbook must be verified in Phase 7 on a clean Windows 11 VM.
