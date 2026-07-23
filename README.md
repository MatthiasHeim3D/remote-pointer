# Remote Pointer

Remote Pointer is a side-band Windows 11 pointer application. It exchanges only normalized pointer coordinates and session metadata through an internal relay; it does not capture screens or inject input.

Phases 1-5 provide the contracts, desktop overlays, relay, and end-to-end workflow. Phase 6 hardens that workflow with HTTPS enforcement, DPAPI-protected crash recovery, structured audit events, safe error boundaries, dependency auditing, and a threat model.

## Build and local test

Prerequisites are Windows 11 and the .NET 10 SDK.

```powershell
dotnet restore RemotePointer.sln
dotnet build RemotePointer.sln --configuration Release --no-restore
dotnet test RemotePointer.sln --configuration Release --no-build
```

Start the local relay, then start two client processes:

```powershell
dotnet run --project src\RemotePointer.Server --launch-profile https
dotnet run --project src\RemotePointer.Client
dotnet run --project src\RemotePointer.Client
```

In the first client, use **Receive pointers**, select a monitor, and create a session. In the second, use **Point at another screen**, enter the pairing code, and request access. Approve the presenter, calibrate, and enable pointing. The receiver overlay remains click-through; the presenter target consumes clicks only while pointing mode is active.

## Small-network deployment

Docker Compose runs the relay behind Caddy HTTPS. Inno Setup produces a self-contained, admin-free per-user client installer. After exporting Caddy's public root certificate, build it with:

```powershell
.\build\Build-Installer.ps1 `
  -ServerUrl https://pointer.internal.example `
  -RelayRootCertificatePath .\relay-root.crt
```

No MSI, WiX, corporate code-signing certificate, machine policy, service, driver, or inbound client firewall rule is required. See [server deployment](docs/server-deployment.md), [client deployment](docs/deployment.md), and [architecture](docs/architecture.md).
