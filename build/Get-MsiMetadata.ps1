[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MsiPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolvedMsi = (Resolve-Path -LiteralPath $MsiPath).Path
$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$database = $windowsInstaller.GetType().InvokeMember(
    'OpenDatabase',
    [System.Reflection.BindingFlags]::InvokeMethod,
    $null,
    $windowsInstaller,
    @($resolvedMsi, 0))

function Get-MsiProperty {
    param([Parameter(Mandatory)][string]$Name)

    $view = $database.OpenView(
        "SELECT `Value` FROM `Property` WHERE `Property` = '$Name'")
    $null = $view.Execute()
    $record = $view.Fetch()
    if ($null -eq $record) {
        return $null
    }

    return $record.StringData(1)
}

$signature = Get-AuthenticodeSignature -LiteralPath $resolvedMsi
$signerSubject = if ($null -ne $signature.SignerCertificate) {
    $signature.SignerCertificate.Subject
}
else {
    $null
}
$timeStamperSubject = if ($null -ne $signature.TimeStamperCertificate) {
    $signature.TimeStamperCertificate.Subject
}
else {
    $null
}
[pscustomobject]@{
    Path = $resolvedMsi
    ProductName = Get-MsiProperty -Name 'ProductName'
    ProductVersion = Get-MsiProperty -Name 'ProductVersion'
    ProductCode = Get-MsiProperty -Name 'ProductCode'
    UpgradeCode = Get-MsiProperty -Name 'UpgradeCode'
    Sha256 = (Get-FileHash -LiteralPath $resolvedMsi -Algorithm SHA256).Hash
    SignatureStatus = $signature.Status.ToString()
    SignerSubject = $signerSubject
    TimeStamperSubject = $timeStamperSubject
}
