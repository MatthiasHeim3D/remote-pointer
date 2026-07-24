[CmdletBinding()]
param()

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
$previousServerOverride = [Environment]::GetEnvironmentVariable(
    'REMOTEPOINTER_SERVER_BASEURL',
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

Push-Location $repositoryRoot
try {
    if (Test-TcpPort -HostName 'localhost' -Port $serverPort) {
        throw "Port $serverPort is already in use. Stop the existing development server first."
    }

    $existingClients = @(Get-Process -Name 'RemotePointer.Client' -ErrorAction SilentlyContinue)
    if ($existingClients.Count -gt 0) {
        Write-Warning (
            'Remote Pointer is already running. Exit existing tray instances to avoid confusing them ' +
            'with the two clients started by this script.')
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

    # This process-only override ensures saved production settings cannot redirect
    # either development client away from the local relay.
    $env:REMOTEPOINTER_SERVER_BASEURL = $developmentServerUrl

    Write-Host 'Starting two clients...' -ForegroundColor Cyan
    $firstClient = Start-Process `
        -FilePath $clientExecutable `
        -WorkingDirectory $clientDirectory `
        -PassThru
    $processes.Add($firstClient)
    $secondClient = Start-Process `
        -FilePath $clientExecutable `
        -WorkingDirectory $clientDirectory `
        -PassThru
    $processes.Add($secondClient)

    Write-Host ''
    Write-Host "Server PID: $($server.Id)" -ForegroundColor Green
    Write-Host "Client PIDs: $($firstClient.Id), $($secondClient.Id)" -ForegroundColor Green
    Write-Host 'Close both clients or press Ctrl+C to stop the development session.'
    Write-Host "Server logs: $logDirectory"

    while ($true) {
        $server.Refresh()
        if ($server.HasExited) {
            throw "The development server stopped unexpectedly. See '$serverErrorLog'."
        }

        $firstClient.Refresh()
        $secondClient.Refresh()
        if ($firstClient.HasExited -and $secondClient.HasExited) {
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
        'REMOTEPOINTER_SERVER_BASEURL',
        $previousServerOverride,
        [EnvironmentVariableTarget]::Process)
    Pop-Location
}
