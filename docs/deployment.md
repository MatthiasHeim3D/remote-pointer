# Windows client deployment

Remote Pointer is packaged as a self-contained x64 application in a per-user Inno Setup installer. Setup writes to `%LocalAppData%\Programs\Remote Pointer`, creates a current-user Start menu shortcut, and does not request administrator rights.

The relay URL is built into the installer. If the relay's HTTPS certificate chains to a publicly trusted CA (for example, a hostname fronted by a Cloudflare Tunnel), omit `-RelayRootCertificatePath` — Windows already trusts that certificate and no root needs installing:

```powershell
.\build\Build-Installer.ps1 `
  -Version 1.0.0 `
  -ServerUrl https://pointer.example.com
```

If the relay instead uses Caddy's private CA (see [server-deployment.md](server-deployment.md)), export its root certificate and pass it so the installer can trust it:

```powershell
.\build\Build-Installer.ps1 `
  -Version 1.0.0 `
  -ServerUrl https://pointer.internal.example `
  -RelayRootCertificatePath .\relay-root.crt
```

Build prerequisites are the .NET 10 SDK and Inno Setup 6. The output is:

```text
artifacts\installer\RemotePointer.Client-1.0.0-x64-Setup.exe
artifacts\installer\RemotePointer.Client-1.0.0-x64-Setup.exe.sha256
```

No MSI, WiX, signing certificate, machine configuration, service, driver, or inbound firewall rule is involved. Because the installer is intentionally unsigned, distribute it and its SHA-256 file from a restricted internal share or another authenticated internal channel.

## Install

Run the setup executable as the user who will use Remote Pointer. If the installer was built with `-RelayRootCertificatePath`, leave the HTTPS certificate task selected — it adds only Caddy's **public** root certificate to `Cert:\CurrentUser\Root`; the CA private key never leaves the Docker server. Installers built without that flag (public-CA relay hostnames) have no certificate task at all, since Windows already trusts the relay's certificate chain.

The client uses normal Windows certificate validation and still refuses non-HTTPS relay URLs. Changing the relay hostname, or replacing Caddy's data volume for a Caddy-fronted relay, requires exporting the new root and rebuilding the installer.

For a quiet current-user install:

```powershell
.\RemotePointer.Client-1.0.0-x64-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-
```

Uninstall from Windows Settings, or run:

```powershell
& "$env:LOCALAPPDATA\Programs\Remote Pointer\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Uninstall leaves `%LocalAppData%\RemotePointer` audit and protected recovery data, and it leaves the trusted relay root in the user's certificate store. That avoids breaking another installed version. Remove the root manually only after no internal service depends on it.

## Installer smoke test

The following installs, validates the HTTPS configuration, and uninstalls without elevation:

```powershell
.\build\Test-Installer.ps1 `
  -SetupPath .\artifacts\installer\RemotePointer.Client-1.0.0-x64-Setup.exe
```
