# Windows client deployment

Remote Annotate is packaged as a self-contained x64 application in an Inno Setup installer that can install for one user or for the whole machine. Setup asks which on its first page and defaults to the current user, so the normal path writes to `%LocalAppData%\Programs\Remote Annotate`, creates a current-user Start menu shortcut, and never requests administrator rights. Choosing **Install for all users** triggers a UAC prompt and installs to `%ProgramFiles%\Remote Annotate` with an all-users Start menu shortcut.

Either way the client's own data stays per-user under `%LocalAppData%\RemoteAnnotate` — settings, client identity, DPAPI-protected credentials, calibrations, and audit logs. An all-users install therefore shares only the program files: each account still gets its own first-run setup, its own relay address and server password, and its own "Launch at startup" registration under `HKCU`.

The relay URL is not built into the installer. On first launch, the client opens
Settings and asks the user to enter the HTTPS relay address. Tell users the server
password for your relay at the same time: they enter it in the same screen, and by
default a relay refuses clients that have none. See
[server-deployment.md](server-deployment.md#server-passwords) for what the password
does. If the relay's HTTPS
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
artifacts\installer\RemoteAnnotate.Client-<version>-x64-Setup.exe
artifacts\installer\RemoteAnnotate.Client-<version>-x64-Setup.exe.sha256
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

Run the setup executable as the user who will use Remote Annotate and pick an install mode on the first page. Pick **Install for me only** unless you are setting up a shared PC and hold local administrator rights; it is the preselected option and needs no elevation.

If the installer was built with `-RelayRootCertificatePath`, leave the HTTPS certificate task selected — it adds only Caddy's **public** root certificate; the CA private key never leaves the Docker server. The store follows the install mode: a per-user install writes to `Cert:\CurrentUser\Root`, an all-users install writes to `Cert:\LocalMachine\Root` so every account on the PC trusts the relay. Installers built without that flag (public-CA relay hostnames) have no certificate task at all, since Windows already trusts the relay's certificate chain.

The client uses normal Windows certificate validation and still refuses non-HTTPS relay URLs. Changing the relay hostname is done in the client's Settings. Replacing Caddy's data volume for a Caddy-fronted relay requires exporting the new root and rebuilding the installer.

For a quiet current-user install:

```powershell
.\RemoteAnnotate.Client-1.0.0-x64-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER
```

For a quiet machine-wide install, run the same command with `/ALLUSERS` from an already elevated session — silent setup cannot show a UAC prompt:

```powershell
.\RemoteAnnotate.Client-1.0.0-x64-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /ALLUSERS
```

Uninstall from Windows Settings, or run the uninstaller from wherever the install landed:

```powershell
& "$env:LOCALAPPDATA\Programs\Remote Annotate\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
& "$env:ProgramFiles\Remote Annotate\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

An interactive uninstall asks whether to also delete `%LocalAppData%\RemoteAnnotate` (saved settings, profile picture cache, audit and protected recovery data); answering No, or uninstalling silently (`/SUPPRESSMSGBOXES`), leaves it in place so another installed version keeps working. Because that data and the `HKCU` startup registration are per-account, uninstalling an all-users install only clears them for the account running the uninstaller; other accounts keep their own copies, and their startup entries simply stop resolving. The uninstaller never removes the trusted relay root from either certificate store — remove that manually, and only after no internal service depends on it.

## Installer smoke test

The following installs, validates that no relay address is preconfigured, and uninstalls without elevation:

```powershell
.\build\Test-Installer.ps1 `
  -SetupPath .\artifacts\installer\RemoteAnnotate.Client-1.0.0-x64-Setup.exe
```

To cover the machine-wide path, run the same script with `-Scope AllUsers` from an elevated session.
