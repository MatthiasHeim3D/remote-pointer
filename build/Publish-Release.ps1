[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [string]$Remote = 'origin',
    [string]$Branch = 'main'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$Command,

        [Parameter()]
        [string[]]$Arguments = @()
    )

    $output = & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        $displayCommand = (@($Command) + $Arguments) -join ' '
        throw "Command failed with exit code ${LASTEXITCODE}: $displayCommand"
    }

    return $output
}

$repositoryRoot = (Invoke-Checked -Command 'git' -Arguments @('rev-parse', '--show-toplevel')).Trim()

Push-Location $repositoryRoot
try {
    $currentBranch = (Invoke-Checked -Command 'git' -Arguments @('branch', '--show-current')).Trim()
    if ($currentBranch -ne $Branch) {
        throw "Releases must be created from branch '$Branch'; the current branch is '$currentBranch'."
    }

    $workingTreeChanges = @(Invoke-Checked -Command 'git' -Arguments @('status', '--porcelain'))
    if ($workingTreeChanges.Count -gt 0) {
        throw 'The working tree is not clean. Commit or stash all changes before publishing a release.'
    }

    Invoke-Checked -Command 'dotnet' -Arguments @('tool', 'restore') | Out-Host

    $headCommit = (Invoke-Checked -Command 'git' -Arguments @('rev-parse', 'HEAD')).Trim()
    $remoteBranch = @(Invoke-Checked -Command 'git' -Arguments @(
        'ls-remote',
        '--heads',
        $Remote,
        "refs/heads/$Branch"
    ))

    if ($remoteBranch.Count -ne 1) {
        throw "Could not resolve exactly one '$Remote/$Branch' branch. Push the branch before publishing a release."
    }

    $remoteCommit = ($remoteBranch[0] -split '\s+')[0]
    if ($remoteCommit -ne $headCommit) {
        throw "HEAD ($headCommit) is not the tip of '$Remote/$Branch' ($remoteCommit). Push or synchronize the branch first."
    }

    $releaseTag = (Invoke-Checked -Command 'dotnet' -Arguments @(
        'nbgv',
        'tag',
        'HEAD',
        '--what-if'
    )).Trim()

    if ([string]::IsNullOrWhiteSpace($releaseTag) -or -not $releaseTag.StartsWith('v', [StringComparison]::Ordinal)) {
        throw "Nerdbank.GitVersioning returned an unexpected release tag: '$releaseTag'."
    }

    & git show-ref --verify --quiet "refs/tags/$releaseTag"
    if ($LASTEXITCODE -eq 0) {
        $localTagCommit = (Invoke-Checked -Command 'git' -Arguments @(
            'rev-list',
            '-n',
            '1',
            "refs/tags/$releaseTag"
        )).Trim()
        if ($localTagCommit -ne $headCommit) {
            throw "The local tag '$releaseTag' already exists on another commit ($localTagCommit)."
        }

        $createLocalTag = $false
        Write-Host "Local tag '$releaseTag' already points to HEAD; it will be reused."
    }
    elseif ($LASTEXITCODE -eq 1) {
        $createLocalTag = $true
    }
    else {
        throw "Could not check whether the local tag '$releaseTag' exists."
    }

    $remoteTag = @(Invoke-Checked -Command 'git' -Arguments @(
        'ls-remote',
        '--tags',
        $Remote,
        "refs/tags/$releaseTag"
    ))
    if ($remoteTag.Count -gt 0) {
        throw "The tag '$releaseTag' already exists on '$Remote'. No release was created."
    }

    Write-Host "Release tag: $releaseTag"
    Write-Host "Commit:      $headCommit"
    Write-Host "Remote:      $Remote"

    if ($PSCmdlet.ShouldProcess(
        "$Remote/$releaseTag at $headCommit",
        'Create or reuse the NB.GV release tag and push it'
    )) {
        if ($createLocalTag) {
            Invoke-Checked -Command 'dotnet' -Arguments @('nbgv', 'tag', 'HEAD') | Out-Host
        }

        try {
            Invoke-Checked -Command 'git' -Arguments @(
                'push',
                $Remote,
                "refs/tags/${releaseTag}:refs/tags/${releaseTag}"
            ) | Out-Host
        }
        catch {
            Write-Warning "The push failed. Local tag '$releaseTag' remains and was not deleted."
            throw
        }

        Write-Host "Release '$releaseTag' published. Its tag push will start the release workflow."
    }
}
finally {
    Pop-Location
}
