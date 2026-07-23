[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [uri]$ServerUrl
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($ServerUrl.Scheme -ne [System.Uri]::UriSchemeHttps) {
    throw 'ServerUrl must use HTTPS.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Machine-wide configuration requires an elevated PowerShell session.'
}

$configurationDirectory = Join-Path $env:ProgramData 'RemotePointer'
$configurationPath = Join-Path $configurationDirectory 'clientsettings.json'
$temporaryPath = "$configurationPath.new"
$configuration = [ordered]@{
    Server = [ordered]@{
        BaseUrl = $ServerUrl.AbsoluteUri.TrimEnd('/')
    }
}

if ($PSCmdlet.ShouldProcess($configurationPath, 'Write Remote Pointer machine configuration')) {
    New-Item -ItemType Directory -Path $configurationDirectory -Force | Out-Null
    $configuration | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $temporaryPath -Encoding utf8
    Move-Item -LiteralPath $temporaryPath -Destination $configurationPath -Force
    Write-Output $configurationPath
}
