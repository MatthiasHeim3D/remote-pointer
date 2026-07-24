# Windows client deployment

Remote Pointer is packaged as a self-contained x64 application in a per-user Inno Setup installer. Setup writes to `%LocalAppData%\Programs\Remote Pointer`, creates a current-user Start menu shortcut, and does not request administrator rights.

The relay URL is not built into the installer. On first launch, the client opens
Settings and asks the user to enter the HTTPS relay address. If the relay's HTTPS
certificate chains to a publicly trusted CA (for example, a hostname fronted by a
Cloudflare Tunnel), omit `-RelayRootCertificatePath` — Windows already trusts that
certificate and no root needs installing:

```powershell
.\build\Build-Installer.ps1
```

If the relay instead uses Caddy's private CA (see [server-deployment.md](server-deployment.md)), export its root certificate and pass it so the installer can trust it:

```powershell
.\build\Build-Installer.ps1 `
  -RelayRootCertificatePath .\relay-root.crt
```

Build prerequisites are the .NET 10 SDK and Inno Setup 6. Nerdbank.GitVersioning calculates the installer version from the repository's shared root `version.json`; no version argument is required. The output is:

```text
artifacts\installer\RemotePointer.Client-<version>-x64-Setup.exe
artifacts\installer\RemotePointer.Client-<version>-x64-Setup.exe.sha256
```

No MSI, WiX, signing certificate, machine configuration, service, driver, or inbound firewall rule is involved. Because the installer is intentionally unsigned, distribute it and its SHA-256 file from a restricted internal share or another authenticated internal channel.

## Publish a relay image

Normal branch pushes do not run the relay-image workflow. To publish a release, start from a clean `main` branch whose current commit is already the tip of `origin/main`, then run:

```powershell
.\build\Publish-Release.ps1
```

The script restores the repository-pinned Nerdbank.GitVersioning tool, uses `nbgv tag` to calculate and create the current commit's version tag (for example, `v1.0.14`), and pushes only that tag. The tag push starts the GitHub Actions workflow, which verifies the tag against NB.GV before publishing the versioned relay image and `latest`.

Preview the release without creating or pushing a tag with:

```powershell
.\build\Publish-Release.ps1 -WhatIf
```

## Install

Run the setup executable as the user who will use Remote Pointer. If the installer was built with `-RelayRootCertificatePath`, leave the HTTPS certificate task selected — it adds only Caddy's **public** root certificate to `Cert:\CurrentUser\Root`; the CA private key never leaves the Docker server. Installers built without that flag (public-CA relay hostnames) have no certificate task at all, since Windows already trusts the relay's certificate chain.

The client uses normal Windows certificate validation and still refuses non-HTTPS relay URLs. Changing the relay hostname is done in the client's Settings. Replacing Caddy's data volume for a Caddy-fronted relay requires exporting the new root and rebuilding the installer.

For a quiet current-user install:

```powershell
.\RemotePointer.Client-1.0.0-x64-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-
```

Uninstall from Windows Settings, or run:

```powershell
& "$env:LOCALAPPDATA\Programs\Remote Pointer\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

An interactive uninstall asks whether to also delete `%LocalAppData%\RemotePointer` (saved settings, profile picture cache, audit and protected recovery data); answering No, or uninstalling silently (`/SUPPRESSMSGBOXES`), leaves it in place so another installed version keeps working. The uninstaller never touches the trusted relay root in the user's certificate store — remove that manually, and only after no internal service depends on it.

## Installer smoke test

The following installs, validates that no relay address is preconfigured, and uninstalls without elevation:

```powershell
.\build\Test-Installer.ps1 `
  -SetupPath .\artifacts\installer\RemotePointer.Client-1.0.0-x64-Setup.exe
```
