[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SetupPath,

    [ValidateSet('CurrentUser', 'AllUsers')]
    [string]$Scope = 'CurrentUser',

    [switch]$SkipCertificateTrust
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolvedSetup = (Resolve-Path -LiteralPath $SetupPath).Path

if ($Scope -eq 'AllUsers') {
    $isElevated = ([Security.Principal.WindowsPrincipal]::new(
            [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isElevated) {
        throw 'An all-users check must run from an elevated session; setup cannot show a UAC prompt in silent mode.'
    }
    $installDirectory = Join-Path $env:ProgramFiles 'Remote Annotate'
    $scopeArgument = '/ALLUSERS'
}
else {
    $installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Remote Annotate'
    $scopeArgument = '/CURRENTUSER'
}

$executablePath = Join-Path $installDirectory 'RemoteAnnotate.Client.exe'
$uninstallerPath = Join-Path $installDirectory 'unins000.exe'

$installArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- $scopeArgument"
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
if (-not [string]::IsNullOrEmpty($settings.Server.BaseUrl)) {
    throw 'A fresh install must not contain a preconfigured relay URL.'
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

if ($Scope -eq 'AllUsers') {
    Write-Output "All-users install and uninstall checks passed under $installDirectory."
}
else {
    Write-Output 'Per-user install and uninstall checks passed without elevation.'
}
