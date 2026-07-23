[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SetupPath,

    [switch]$SkipCertificateTrust
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolvedSetup = (Resolve-Path -LiteralPath $SetupPath).Path
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Remote Pointer'
$executablePath = Join-Path $installDirectory 'RemotePointer.Client.exe'
$uninstallerPath = Join-Path $installDirectory 'unins000.exe'

$installArguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
if ($SkipCertificateTrust) {
    $installArguments += ' /MERGETASKS="!trustrelay"'
}

$install = Start-Process -FilePath $resolvedSetup `
    -ArgumentList $installArguments `
    -Wait `
    -PassThru
if ($install.ExitCode -ne 0) {
    throw "Setup failed with exit code $($install.ExitCode)."
}
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "The installed executable was not found: $executablePath"
}

$settings = Get-Content -Raw -LiteralPath (Join-Path $installDirectory 'appsettings.json') |
    ConvertFrom-Json
if (-not $settings.Server.BaseUrl.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The installed relay URL is not HTTPS.'
}

$uninstall = Start-Process -FilePath $uninstallerPath `
    -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' `
    -Wait `
    -PassThru
if ($uninstall.ExitCode -ne 0) {
    throw "Uninstall failed with exit code $($uninstall.ExitCode)."
}
if (Test-Path -LiteralPath $executablePath) {
    throw 'Uninstall did not remove the application executable.'
}

Write-Output 'Per-user install and uninstall checks passed without elevation.'
