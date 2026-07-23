# Client packaging and corporate deployment

Phase 7 ships the Windows client as a self-contained x64 MSI. The package installs per machine to `C:\Program Files\Remote Pointer`, creates an all-users Start menu shortcut, supports MSI major upgrades, and requires elevation only for installation, upgrade, or removal. The installed client runs as a standard user.

## Build a release package

The build uses the WiX 5.0.0 MSBuild SDK. WiX is restored as a build dependency and is not installed on endpoints or included in the client payload.

A distributable build requires an organization-issued Authenticode certificate:

```powershell
.\build\Build-Installer.ps1 `
  -Version 1.0.0 `
  -CertificateThumbprint <certificate-thumbprint> `
  -TimestampUrl https://timestamp.digicert.com
```

This publishes and signs the self-contained client, embeds it in the MSI, signs the MSI, and rejects the build if either signature does not validate. `-AllowUnsigned` exists only for local packaging validation and its output must not be distributed.

Release output:

```text
artifacts\publish\client\win-x64\
artifacts\installer\RemotePointer.Client-<version>-x64.msi
```

Use three-part MSI versions and increment at least one of the first three fields for every release. The stable upgrade code is authored in `installer/RemotePointer.Client.Installer/Package.wxs`; do not change it unless side-by-side products are intentionally required.

## Machine-wide client configuration

The client reads configuration in this order, with later sources taking precedence:

1. `appsettings.json` beside the executable.
2. `%ProgramData%\RemotePointer\clientsettings.json`.
3. `REMOTEPOINTER_SERVER_BASEURL` in the client process environment.

Configure the approved relay URL from an elevated PowerShell session:

```powershell
.\build\Set-MachineConfiguration.ps1 `
  -ServerUrl https://pointer.internal.example
```

The machine file contains only non-secret policy:

```json
{
  "Server": {
    "BaseUrl": "https://pointer.internal.example"
  }
}
```

The MSI deliberately does not own the ProgramData configuration. Installation, major upgrade, repair, and uninstall therefore cannot overwrite or delete it. Deploy its ACL using normal organization policy: administrators and SYSTEM may modify it; standard users need read access only. HTTPS is mandatory and normal Windows certificate validation remains enabled.

Session recovery tokens and audit records remain per-user under `%LocalAppData%\RemotePointer`. They are not MSI resources. Uninstall removes application files and shortcuts while leaving audit records available for the organization's retention process.

## Silent commands

Install or upgrade:

```powershell
msiexec.exe /i RemotePointer.Client-1.0.0-x64.msi /qn /norestart /L*v C:\Windows\Temp\RemotePointer-install.log
```

Uninstall using the deployed MSI:

```powershell
msiexec.exe /x RemotePointer.Client-1.0.0-x64.msi /qn /norestart /L*v C:\Windows\Temp\RemotePointer-uninstall.log
```

Windows Installer returns `0` for success and `3010` for success requiring restart. Remote Pointer does not normally require a restart. Before upgrade or uninstall, close `RemotePointer.Client.exe`; deployment tooling may otherwise report a files-in-use condition.

## Upgrade and rollback

- A higher three-part version performs an MSI major upgrade and preserves the ProgramData configuration and all per-user audit/session data.
- Downgrades are blocked. Roll back by producing a new, higher version containing the previous approved application payload.
- Test an upgrade with both old and new signed MSIs by passing `-PreviousMsiPath` to `build\Test-Installer.ps1`.
- Do not reuse a version for different production payloads.

## Intune

Preferred packaging is an Intune Win32 app so deployment can participate in Enrollment Status Page, dependencies, delivery optimization, and richer detection. Wrap the signed MSI with the Microsoft Win32 Content Prep Tool, then use:

- Install: `msiexec.exe /i RemotePointer.Client-1.0.0-x64.msi /qn /norestart`
- Uninstall: `msiexec.exe /x {ProductCode} /qn /norestart`
- Install behavior: System
- Requirements: Windows 11, x64
- Detection: MSI product code and product version, or file version at `C:\Program Files\Remote Pointer\RemotePointer.Client.exe`
- Return codes: retain Intune defaults for `0` and `3010`

Deploy `clientsettings.json` as a separate device configuration/remediation before making the app required. Microsoft documents MSI product-code, file, and registry detection for Win32 apps at <https://learn.microsoft.com/en-us/intune/apps/apps-win32-add>.

## Configuration Manager / SCCM

Create an Application with the signed MSI as a Windows Installer deployment type:

- Installation behavior: Install for system
- Logon requirement: Whether or not a user is logged on
- User interaction: Hidden
- Requirement: 64-bit Windows 11
- Detection: imported MSI product code
- Install/uninstall commands: the silent commands above
- Maximum runtime: 15 minutes

Distribute the ProgramData machine configuration as a configuration item/baseline or a small signed PowerShell deployment. Supersede the previous application version with uninstall disabled so MSI performs the major upgrade.

## Firewall and proxy requirements

Client endpoints need only outbound TCP 443 to the approved relay FQDN. SignalR uses WSS/WebSockets when available and HTTPS fallback transports otherwise. No inbound endpoint rule, local listening port, service, driver, or conferencing-application exception is required.

The relay tier needs inbound TCP 443 from approved client networks and outbound access only for organization-required logging, monitoring, certificate, or identity dependencies. Permit WebSocket upgrades through reverse proxies and keep session affinity if multiple relay instances are introduced while session state remains in memory.

TLS inspection must present a certificate chain trusted by Windows and valid for the configured relay hostname. Do not bypass validation.

## Clean Windows 11 acceptance

On an elevated, disposable Windows 11 VM with the organization root and signing certificates installed:

```powershell
.\build\Test-Installer.ps1 `
  -MsiPath .\artifacts\installer\RemotePointer.Client-1.0.0-x64.msi `
  -ServerUrl https://pointer.internal.example
```

For upgrade coverage, add `-PreviousMsiPath <old-signed-msi>`. The script verifies the package signature, silent install/upgrade, installed executable, machine configuration preservation, silent uninstall, application-file removal, and preservation of a per-user audit sentinel. Keep its MSI logs with release evidence.

Also run the Phase 5/6 functional and display matrix as a standard user. Validate signatures independently:

```powershell
Get-AuthenticodeSignature .\artifacts\installer\RemotePointer.Client-1.0.0-x64.msi
signtool.exe verify /pa /all /v .\artifacts\installer\RemotePointer.Client-1.0.0-x64.msi
```

Do not approve a release until both the clean-VM test and signature validation are green.
