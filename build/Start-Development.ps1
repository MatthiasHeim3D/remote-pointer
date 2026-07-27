<#
.SYNOPSIS
    Starts the development relay and a number of desktop clients that can reach each other.

.DESCRIPTION
    Each client runs against its own throwaway data directory, so several of them behave like
    separate users on separate machines: distinct identities, distinct preferences, distinct
    saved credentials. Nothing is shared with each other or with an installed release.

    The relay is started with a server password and every client is seeded with the key derived
    from it, which is what gets them onto the relay at all. They all start in the same room, so
    they see one another in the host directory. The directories are deleted when the session ends
    unless -KeepClientData is given.

.PARAMETER ClientCount
    How many clients to start. Defaults to 2.

.PARAMETER ServerPassword
    The relay's password, which all clients present. Must be at least 8 characters, matching the
    rule the client and the relay both apply.

.PARAMETER KeepClientData
    Leaves the per-client data directories in place for inspection instead of deleting them.

.EXAMPLE
    .\build\Start-Development.ps1 -ClientCount 3
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingPlainTextForPassword',
    'ServerPassword',
    Justification = 'A shared development password the script prints on purpose so it can be ' +
        'typed into a client. It guards nothing, and PBKDF2 needs the characters anyway.')]
[CmdletBinding()]
param(
    [ValidateRange(1, 8)]
    [int]$ClientCount = 2,

    [ValidateNotNullOrEmpty()]
    [string]$ServerPassword = 'remote-pointer-dev',

    [switch]$KeepClientData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$configuration = 'Debug'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'RemotePointer.sln'
$serverProject = 'src\RemotePointer.Server\RemotePointer.Server.csproj'
$clientDirectory = Join-Path $repositoryRoot (
    "src\RemotePointer.Client\bin\$configuration\net10.0-windows\win-x64")
$clientExecutable = Join-Path $clientDirectory 'RemotePointer.Client.exe'
$developmentServerUrl = 'https://localhost:7243'
$serverPort = 7243
$processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$clientProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$clientDataRoot = $null

# Mirrors of the shared derivation constants. Assert-ClientCryptoConstant checks each one against
# the source below, so a change there fails this script loudly instead of quietly seeding clients
# with a key the relay will not accept.
$passwordSaltText = 'RemotePointer.ServerPassword.v1'
$passwordIterations = 210000
$passwordKeyBytes = 32
$protectionEntropyText = 'RemotePointer.SessionCredential.v1'
$minimumPasswordLength = 8
$dataDirectoryVariable = 'REMOTEPOINTER_DATA_DIRECTORY'
$serverUrlVariable = 'REMOTEPOINTER_SERVER_BASEURL'
$serverPasswordVariable = 'Access__ServerPassword'
$developmentRoom = 'general'

$previousServerOverride = [Environment]::GetEnvironmentVariable(
    $serverUrlVariable,
    [EnvironmentVariableTarget]::Process)
$previousDataDirectory = [Environment]::GetEnvironmentVariable(
    $dataDirectoryVariable,
    [EnvironmentVariableTarget]::Process)
$previousServerPassword = [Environment]::GetEnvironmentVariable(
    $serverPasswordVariable,
    [EnvironmentVariableTarget]::Process)

function Test-TcpPort {
    param(
        [Parameter(Mandatory)]
        [string]$HostName,

        [Parameter(Mandatory)]
        [int]$Port,

        [int]$TimeoutMilliseconds = 250
    )

    $tcpClient = [System.Net.Sockets.TcpClient]::new()
    try {
        $connectTask = $tcpClient.ConnectAsync($HostName, $Port)
        return $connectTask.Wait($TimeoutMilliseconds) -and $tcpClient.Connected
    }
    catch {
        return $false
    }
    finally {
        $tcpClient.Dispose()
    }
}

function Stop-OwnedProcess {
    param([System.Diagnostics.Process]$Process)

    try {
        $Process.Refresh()
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        }
    }
    catch [System.InvalidOperationException] {
        # The process already exited between the state check and cleanup.
    }
}

function Assert-ClientCryptoConstant {
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath,

        [Parameter(Mandatory)]
        [string]$Pattern,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $path = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected '$RelativePath' to exist so this script can verify $Description."
    }

    if (-not (Select-String -LiteralPath $path -Pattern $Pattern -SimpleMatch -Quiet)) {
        throw (
            "$RelativePath no longer contains $Description. This script reproduces the client's " +
            'password derivation to seed a shared password; update both together.')
    }
}

function Get-ServerPasswordKey {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSAvoidUsingPlainTextForPassword',
        'Password',
        Justification = 'Mirrors the shared derivation, which takes the password characters.')]
    param(
        [Parameter(Mandatory)]
        [string]$Password
    )

    # The relay only ever sees this derived key, never the password. It derives the same value
    # from its own configured password and admits a client precisely when the two match.
    $salt = [System.Text.Encoding]::UTF8.GetBytes($passwordSaltText)
    $passwordBytes = [System.Text.Encoding]::UTF8.GetBytes($Password.Trim())
    $derive = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
        $passwordBytes,
        $salt,
        $passwordIterations,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $keyBytes = $derive.GetBytes($passwordKeyBytes)
    }
    finally {
        $derive.Dispose()
    }

    return [Convert]::ToBase64String($keyBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function New-ClientDataDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$UserName,

        [Parameter(Mandatory)]
        [string]$DerivedKey
    )

    $sessionDirectory = Join-Path $Path 'Sessions'
    New-Item -ItemType Directory -Path $sessionDirectory -Force | Out-Null

    # The room is plain text on purpose, unlike the key below: it is a label the client shows
    # back in Settings, not a secret, and every development client starts in the same one.
    $preferences = [ordered]@{
        serverAddress               = $developmentServerUrl
        userName                    = $UserName
        profilePicturePath          = ''
        maximumAnnotatorConnections = 2
        launchAtStartup             = $false
        selectedDisplayId           = ''
        showUsageHints              = $false
        hostAvailable               = $false
        hasShownUsageHints          = $true
        room                        = $developmentRoom
    }
    $preferencesJson = $preferences | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText(
        (Join-Path $Path 'user-settings.json'),
        $preferencesJson,
        [System.Text.UTF8Encoding]::new($false))

    # Same shape the client writes: the derived key under DPAPI for the current Windows account,
    # with the client's fixed entropy. The account's master key is untouched, so deleting these
    # directories is the whole of the cleanup.
    $entropy = [System.Text.Encoding]::UTF8.GetBytes($protectionEntropyText)
    $plaintext = [System.Text.Encoding]::UTF8.GetBytes($DerivedKey)
    $protectedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
        $plaintext,
        $entropy,
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    [System.IO.File]::WriteAllBytes(
        (Join-Path $sessionDirectory 'server-password.key'),
        $protectedBytes)
}

Push-Location $repositoryRoot
try {
    Add-Type -AssemblyName System.Security

    if ($ServerPassword.Trim().Length -lt $minimumPasswordLength) {
        throw "The server password must be at least $minimumPasswordLength characters."
    }

    Assert-ClientCryptoConstant `
        -RelativePath 'src\RemotePointer.Contracts\Security\ServerPasswordKey.cs' `
        -Pattern "`"$passwordSaltText`"" `
        -Description 'the expected password salt'
    Assert-ClientCryptoConstant `
        -RelativePath 'src\RemotePointer.Contracts\Security\ServerPasswordKey.cs' `
        -Pattern 'Iterations = 210_000' `
        -Description 'the expected password iteration count'
    Assert-ClientCryptoConstant `
        -RelativePath 'src\RemotePointer.Contracts\Security\ServerPasswordKey.cs' `
        -Pattern "KeyBytes = $passwordKeyBytes" `
        -Description 'the expected derived key length'
    Assert-ClientCryptoConstant `
        -RelativePath 'src\RemotePointer.Client\Services\DpapiDataProtector.cs' `
        -Pattern "`"$protectionEntropyText`"" `
        -Description 'the expected data-protection entropy'
    Assert-ClientCryptoConstant `
        -RelativePath 'src\RemotePointer.Client\Configuration\ClientDataDirectory.cs' `
        -Pattern "OverrideVariableName = `"$dataDirectoryVariable`"" `
        -Description 'the expected data-directory override variable'

    if (Test-TcpPort -HostName 'localhost' -Port $serverPort) {
        throw "Port $serverPort is already in use. Stop the existing development server first."
    }

    $existingClients = @(Get-Process -Name 'RemotePointer.Client' -ErrorAction SilentlyContinue)
    if ($existingClients.Count -gt 0) {
        Write-Warning (
            'Remote Pointer is already running. Its settings are untouched by this script, but ' +
            'its tray icon is easy to confuse with the development clients.')
    }

    Write-Host "Building RemotePointer.sln ($configuration development build)..." -ForegroundColor Cyan
    & dotnet build $solutionPath --configuration $configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Solution build failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $clientExecutable -PathType Leaf)) {
        throw "The client executable was not produced at '$clientExecutable'."
    }

    $logDirectory = Join-Path $repositoryRoot 'artifacts\development'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $serverOutputLog = Join-Path $logDirectory 'server.stdout.log'
    $serverErrorLog = Join-Path $logDirectory 'server.stderr.log'
    $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source

    Write-Host "Starting relay at $developmentServerUrl..." -ForegroundColor Cyan

    # The relay inherits this and derives the same key the clients are seeded with, so the
    # development session exercises the real front door rather than an open relay.
    [Environment]::SetEnvironmentVariable(
        $serverPasswordVariable,
        $ServerPassword,
        [EnvironmentVariableTarget]::Process)
    $server = Start-Process `
        -FilePath $dotnetPath `
        -ArgumentList @(
            'run',
            '--project', $serverProject,
            '--configuration', $configuration,
            '--no-build',
            '--launch-profile', 'https') `
        -WorkingDirectory $repositoryRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $serverOutputLog `
        -RedirectStandardError $serverErrorLog `
        -PassThru
    $processes.Add($server)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while (-not (Test-TcpPort -HostName 'localhost' -Port $serverPort)) {
        $server.Refresh()
        if ($server.HasExited) {
            throw "The development server exited early. See '$serverErrorLog'."
        }

        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "The development server did not open port $serverPort within 30 seconds."
        }

        Start-Sleep -Milliseconds 250
    }

    # This process-only override ensures saved production settings cannot redirect a
    # development client away from the local relay.
    $env:REMOTEPOINTER_SERVER_BASEURL = $developmentServerUrl

    $clientDataRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        "RemotePointer.Development\$([Guid]::NewGuid().ToString('N'))")
    New-Item -ItemType Directory -Path $clientDataRoot -Force | Out-Null
    $derivedKey = Get-ServerPasswordKey -Password $ServerPassword

    $clientLabel = if ($ClientCount -eq 1) { 'client' } else { 'clients' }
    Write-Host "Starting $ClientCount $clientLabel..." -ForegroundColor Cyan
    for ($index = 1; $index -le $ClientCount; $index++) {
        $dataDirectory = Join-Path $clientDataRoot "client-$index"
        New-ClientDataDirectory `
            -Path $dataDirectory `
            -UserName "Dev Client $index" `
            -DerivedKey $derivedKey

        # Set immediately before each start: a child inherits this process's environment as it
        # stands at that moment, which is what gives every client its own directory.
        $env:REMOTEPOINTER_DATA_DIRECTORY = $dataDirectory
        $client = Start-Process `
            -FilePath $clientExecutable `
            -WorkingDirectory $clientDirectory `
            -PassThru
        $processes.Add($client)
        $clientProcesses.Add($client)
    }

    [Environment]::SetEnvironmentVariable(
        $dataDirectoryVariable,
        $previousDataDirectory,
        [EnvironmentVariableTarget]::Process)

    Write-Host ''
    Write-Host "Server PID: $($server.Id)" -ForegroundColor Green
    Write-Host "Client PIDs: $(($clientProcesses | ForEach-Object { $_.Id }) -join ', ')" -ForegroundColor Green
    Write-Host "Server password: $ServerPassword" -ForegroundColor Green
    Write-Host "Room: $developmentRoom" -ForegroundColor Green
    Write-Host "Client data: $clientDataRoot"
    Write-Host "Server logs: $logDirectory"
    Write-Host 'Close every client or press Ctrl+C to stop the development session.'

    while ($true) {
        $server.Refresh()
        if ($server.HasExited) {
            throw "The development server stopped unexpectedly. See '$serverErrorLog'."
        }

        $running = $false
        foreach ($client in $clientProcesses) {
            $client.Refresh()
            if (-not $client.HasExited) {
                $running = $true
                break
            }
        }

        if (-not $running) {
            break
        }

        Start-Sleep -Seconds 1
    }
}
finally {
    for ($index = $processes.Count - 1; $index -ge 0; $index--) {
        Stop-OwnedProcess -Process $processes[$index]
        $processes[$index].Dispose()
    }

    [Environment]::SetEnvironmentVariable(
        $serverUrlVariable,
        $previousServerOverride,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $dataDirectoryVariable,
        $previousDataDirectory,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $serverPasswordVariable,
        $previousServerPassword,
        [EnvironmentVariableTarget]::Process)

    if ($clientDataRoot -and (Test-Path -LiteralPath $clientDataRoot)) {
        if ($KeepClientData) {
            Write-Host "Client data kept at $clientDataRoot" -ForegroundColor Yellow
        }
        else {
            # Everything the clients protected lives under here, so removing it leaves nothing
            # behind: DPAPI itself holds no per-file state to purge.
            Remove-Item -LiteralPath $clientDataRoot -Recurse -Force -ErrorAction SilentlyContinue

            # Drop the shared container too, but only once the last concurrent session has gone.
            $sessionContainer = Split-Path -Parent $clientDataRoot
            if ((Test-Path -LiteralPath $sessionContainer) -and
                -not (Get-ChildItem -Force -LiteralPath $sessionContainer)) {
                Remove-Item -LiteralPath $sessionContainer -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Pop-Location
}
