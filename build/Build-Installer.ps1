[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0',

    [Parameter(Mandatory)]
    [uri]$ServerUrl,

    [Parameter(Mandatory)]
    [string]$RelayRootCertificatePath,

    [string]$InnoSetupCompilerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($ServerUrl.Scheme -ne [System.Uri]::UriSchemeHttps) {
    throw 'ServerUrl must use HTTPS.'
}

$resolvedCertificatePath = (Resolve-Path -LiteralPath $RelayRootCertificatePath).Path
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $resolvedCertificatePath)
$basicConstraints = $certificate.Extensions |
    Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension] } |
    Select-Object -First 1
if ($null -eq $basicConstraints -or -not $basicConstraints.CertificateAuthority) {
    throw 'RelayRootCertificatePath must contain a CA certificate.'
}
if ($certificate.HasPrivateKey) {
    throw 'RelayRootCertificatePath must contain only the public certificate, never a CA private key.'
}
if ($certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow) {
    throw 'The relay root certificate is not valid yet.'
}
if ($certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
    throw 'The relay root certificate has expired.'
}
$certificate.Dispose()

if ([string]::IsNullOrWhiteSpace($InnoSetupCompilerPath)) {
    $compilerCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $compilerCommand) {
        $InnoSetupCompilerPath = $compilerCommand.Source
    }
    else {
        $compilerCandidates = @(
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        )
        $InnoSetupCompilerPath = $compilerCandidates |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
    }
}
if ([string]::IsNullOrWhiteSpace($InnoSetupCompilerPath) -or
    -not (Test-Path -LiteralPath $InnoSetupCompilerPath -PathType Leaf)) {
    throw 'Inno Setup 6 was not found. Install it, or pass -InnoSetupCompilerPath.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$clientProject = Join-Path $repositoryRoot 'src\RemotePointer.Client\RemotePointer.Client.csproj'
$installerScript = Join-Path $repositoryRoot 'installer\RemotePointer.Client.iss'
$publishDirectory = Join-Path $repositoryRoot 'artifacts\publish\client\win-x64'
$installerDirectory = Join-Path $repositoryRoot 'artifacts\installer'

& dotnet publish $clientProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    "-p:Version=$Version" `
    '-p:DebugType=None'
if ($LASTEXITCODE -ne 0) {
    throw "Client publish failed with exit code $LASTEXITCODE."
}

$settingsPath = Join-Path $publishDirectory 'appsettings.json'
$settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
$settings.Server.BaseUrl = $ServerUrl.AbsoluteUri.TrimEnd('/')
$settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $settingsPath -Encoding utf8

New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null
& $InnoSetupCompilerPath `
    "/DMyAppVersion=$Version" `
    "/DPublishDir=$publishDirectory" `
    "/DRelayRootCertificate=$resolvedCertificatePath" `
    "/DInstallerOutputDir=$installerDirectory" `
    $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $installerDirectory "RemotePointer.Client-$Version-x64-Setup.exe"
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "The expected installer was not produced: $installerPath"
}

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $installerPath
$hashPath = "$installerPath.sha256"
"$($hash.Hash)  $(Split-Path -Leaf $installerPath)" |
    Set-Content -LiteralPath $hashPath -Encoding ascii

Write-Output $installerPath
Write-Output $hashPath
