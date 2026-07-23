# Deployment

Deployment artifacts are scheduled for Phases 6 and 7. Phase 5 provides a runnable framework-dependent ASP.NET Core relay and Windows client but not a production container or installer.

## Planned client deployment

- Self-contained, signed Windows x64 output targeting `net10.0-windows`.
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

Client settings live in `src/RemotePointer.Client/appsettings.json` and are copied beside the executable. `REMOTEPOINTER_SERVER_BASEURL` overrides `Server:BaseUrl`. Plain HTTP is accepted only for a loopback development address. The default HTTPS development certificate must be trusted for `https://localhost:7243`; certificate validation is never bypassed by the client.

Signing, package upgrade/uninstall behavior, firewall details, and the operational runbook must be verified in Phase 7 on a clean Windows 11 VM.
