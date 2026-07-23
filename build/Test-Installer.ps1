[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MsiPath,

    [uri]$ServerUrl = 'https://pointer.internal.example',

    [string]$PreviousMsiPath,

    [switch]$AllowUnsigned
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Installer acceptance testing requires an elevated PowerShell session.'
}

$resolvedMsi = (Resolve-Path -LiteralPath $MsiPath).Path
$signature = Get-AuthenticodeSignature -LiteralPath $resolvedMsi
if (-not $AllowUnsigned -and $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "The MSI signature is not valid: $($signature.Status)."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$configurationScript = Join-Path $repositoryRoot 'build\Set-MachineConfiguration.ps1'
$installedExecutable = Join-Path $env:ProgramFiles 'Remote Pointer\RemotePointer.Client.exe'
$configurationPath = Join-Path $env:ProgramData 'RemotePointer\clientsettings.json'
$auditDirectory = Join-Path $env:LOCALAPPDATA 'RemotePointer\Logs'
$auditSentinel = Join-Path $auditDirectory 'phase7-uninstall-preservation.test'
$logDirectory = Join-Path $env:TEMP 'RemotePointer-InstallerTests'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

function Invoke-MsiExec {
    param(
        [Parameter(Mandatory)]
        [string]$Arguments,
        [Parameter(Mandatory)]
        [string]$LogName
    )

    $logPath = Join-Path $logDirectory $LogName
    $process = Start-Process -FilePath 'msiexec.exe' `
        -ArgumentList "$Arguments /qn /norestart /L*v `"$logPath`"" `
        -Wait `
        -PassThru
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "msiexec failed with exit code $($process.ExitCode). See $logPath"
    }
}

if (-not [string]::IsNullOrWhiteSpace($PreviousMsiPath)) {
    $resolvedPreviousMsi = (Resolve-Path -LiteralPath $PreviousMsiPath).Path
    Invoke-MsiExec -Arguments "/i `"$resolvedPreviousMsi`"" -LogName 'install-previous.log'
}

& $configurationScript -ServerUrl $ServerUrl
$configurationBefore = Get-Content -Raw -LiteralPath $configurationPath

Invoke-MsiExec -Arguments "/i `"$resolvedMsi`"" -LogName 'install-current.log'
if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
    throw "Installed executable was not found: $installedExecutable"
}
if ((Get-Content -Raw -LiteralPath $configurationPath) -ne $configurationBefore) {
    throw 'The machine configuration changed during install or upgrade.'
}

New-Item -ItemType Directory -Path $auditDirectory -Force | Out-Null
Set-Content -LiteralPath $auditSentinel -Value 'preserve' -Encoding utf8

Invoke-MsiExec -Arguments "/x `"$resolvedMsi`"" -LogName 'uninstall.log'
if (Test-Path -LiteralPath $installedExecutable) {
    throw 'Uninstall did not remove the application executable.'
}
if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
    throw 'Uninstall removed the machine configuration.'
}
if (-not (Test-Path -LiteralPath $auditSentinel -PathType Leaf)) {
    throw 'Uninstall removed a per-user audit record.'
}

Write-Output "Installer acceptance checks passed. Logs: $logDirectory"
