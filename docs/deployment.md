# Deployment

Deployment artifacts are scheduled for Phases 6 and 7. Phase 1 produces source projects only.

## Planned client deployment

- Self-contained, signed Windows x64 output targeting `net10.0-windows`.
- Per-Monitor V2 application manifest.
- MSI or MSIX suitable for silent Intune/SCCM deployment.
- Standard-user operation after installation.
- Machine-wide server URL configuration without storing session secrets in JSON.

## Planned server deployment

- Framework-dependent or containerized ASP.NET Core .NET 10 application.
- Organization-issued TLS certificate and approved interface binding.
- Inbound TCP 443 at the server; clients require outbound HTTPS only.
- JSON configuration with environment-variable overrides.
- Health endpoint at `/health`.

Signing, package upgrade/uninstall behavior, firewall details, and the operational runbook must be verified in Phase 7 on a clean Windows 11 VM.
