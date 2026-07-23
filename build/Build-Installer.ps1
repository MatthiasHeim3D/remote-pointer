[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0',

    [string]$CertificateThumbprint,

    [string]$TimestampUrl = 'https://timestamp.digicert.com',

    [string]$SignToolPath = 'signtool.exe',

    [switch]$AllowUnsigned
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint) -and -not $AllowUnsigned) {
    throw 'A code-signing certificate thumbprint is required. Use -AllowUnsigned only for development validation.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$clientProject = Join-Path $repositoryRoot 'src\RemotePointer.Client\RemotePointer.Client.csproj'
$installerProject = Join-Path $repositoryRoot 'installer\RemotePointer.Client.Installer\RemotePointer.Client.Installer.wixproj'
$publishDirectory = Join-Path $repositoryRoot 'artifacts\publish\client\win-x64'
$installerDirectory = Join-Path $repositoryRoot 'artifacts\installer'

$publishArguments = @(
    'publish', $clientProject,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $publishDirectory,
    "-p:Version=$Version"
)

$isSignedBuild = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
if ($isSignedBuild) {
    $publishArguments += @(
        '-p:EnableCodeSigning=true',
        "-p:CodeSigningCertificateThumbprint=$CertificateThumbprint",
        "-p:CodeSigningTimestampUrl=$TimestampUrl",
        "-p:SignToolPath=$SignToolPath"
    )
}

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "Client publish failed with exit code $LASTEXITCODE."
}

$installerArguments = @(
    'build', $installerProject,
    '--configuration', 'Release',
    "-p:ProductVersion=$Version",
    "-p:PublishDirectory=$publishDirectory",
    "-p:BaseOutputPath=$installerDirectory\"
)
if ($isSignedBuild) {
    $installerArguments += @(
        '-p:EnableInstallerSigning=true',
        "-p:CodeSigningCertificateThumbprint=$CertificateThumbprint",
        "-p:CodeSigningTimestampUrl=$TimestampUrl",
        "-p:SignToolPath=$SignToolPath"
    )
}

& dotnet @installerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Installer build failed with exit code $LASTEXITCODE."
}

$msiPath = Join-Path $installerDirectory "RemotePointer.Client-$Version-x64.msi"
if (-not (Test-Path -LiteralPath $msiPath -PathType Leaf)) {
    throw "The expected installer was not produced: $msiPath"
}

if ($isSignedBuild) {
    foreach ($path in @(
        (Join-Path $publishDirectory 'RemotePointer.Client.exe'),
        $msiPath
    )) {
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw "Authenticode validation failed for $path with status $($signature.Status)."
        }
    }
}
else {
    Write-Warning 'Produced an unsigned development MSI. Do not distribute it.'
}

$metadataScript = Join-Path $repositoryRoot 'build\Get-MsiMetadata.ps1'
$metadataPath = Join-Path $installerDirectory "RemotePointer.Client-$Version-x64.json"
$metadata = & $metadataScript -MsiPath $msiPath
$metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding utf8

Write-Output $msiPath
Write-Output $metadataPath
