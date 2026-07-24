# Remote Pointer

Remote Pointer is a side-band Windows 11 pointer application. It exchanges normalized pointer gestures, deliberate transient text annotations, and session metadata through an internal relay; it does not capture screens or inject input.

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
.\build\Start-Development.ps1
```

The development launcher always builds Debug, starts the HTTPS relay and two clients,
and stops its processes when both clients close or the script is interrupted. Debug
clients allow multiple instances for local testing; Release clients allow only one
running instance.

In the first client, choose **Available**. In each sender client, select it from the visible-receiver list and request access. The receiver must approve every sender and can use **Disconnect all senders** from its dedicated receiving view. A receiver accepts up to its configured sender limit (two by default), remains discoverable while below that limit, and cannot initiate its own sender connection while receiving. Receiver display dimensions synchronize automatically; after approval, calibrate and enable pointing. The receiver overlay remains click-through and each sender target consumes pointer gestures only while pointing mode is active. Left-click highlights, left-drag draws a path, Shift+left-drag draws a line, Shift+left-click creates a text annotation finalized with Enter, right-drag draws a box, and Shift+right-drag draws a circle centered at the initial click. The input-area help panel lists these controls, including Escape, and can always be toggled with `H`. It opens on first use and starts collapsed thereafter; disabling **Show usage hints** hides the collapsed help badge without disabling the shortcut.

## Small-network deployment

Docker Compose runs the relay behind Caddy HTTPS. Inno Setup produces a self-contained, admin-free per-user client installer. After exporting Caddy's public root certificate, build it with:

```powershell
.\build\Build-Installer.ps1 `
  -ServerUrl https://pointer.internal.example `
  -RelayRootCertificatePath .\relay-root.crt
```

No MSI, WiX, corporate code-signing certificate, machine policy, service, driver, or inbound client firewall rule is required. See [server deployment](docs/server-deployment.md), [client deployment](docs/deployment.md), and [architecture](docs/architecture.md).

## Versioning

The repository uses Nerdbank.GitVersioning with the shared root [version.json](version.json). Its `1.0` base version becomes `1.0.<git-height>`, so every commit affects the client and relay together, even when only one changes. The installer build derives its version automatically; do not supply a version number manually. Pushes to `main` publish the relay image to GitHub Container Registry with the same numeric version and `latest` tag.
