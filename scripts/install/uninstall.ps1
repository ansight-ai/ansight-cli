param(
    [string]$InstallRoot = $env:ANSIGHT_INSTALL_ROOT,
    [string]$BinDirectory = $env:ANSIGHT_BIN_DIR,
    [string]$DataDirectory = $env:ANSIGHT_DATA_DIR,
    [string]$ReceiptPath = $env:ANSIGHT_INSTALL_RECEIPT,
    [string]$CliPath = $env:ANSIGHT_UNINSTALL_CLI,
    [switch]$PurgeData,
    [switch]$KeepData,
    [switch]$RemoveSkills,
    [switch]$KeepSkills,
    [switch]$LocalSignOut,
    [switch]$NoSignOut,
    [switch]$KeepStartup,
    [switch]$KeepPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:ANSIGHT_NO_UPDATE_CHECK = '1'

if ($PurgeData -and $KeepData) {
    throw 'Use either -PurgeData or -KeepData, not both.'
}
if ($RemoveSkills -and $KeepSkills) {
    throw 'Use either -RemoveSkills or -KeepSkills, not both.'
}
if ($LocalSignOut -and $NoSignOut) {
    throw 'Use either -LocalSignOut or -NoSignOut, not both.'
}

if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'This uninstaller supports Windows. On macOS or Linux, use uninstall.sh.'
}

$userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$resolvedCliPath = $CliPath
$ansightCommand = Get-Command ansight -ErrorAction SilentlyContinue
if ([string]::IsNullOrWhiteSpace($resolvedCliPath) -and $null -ne $ansightCommand) {
    $resolvedCliPath = $ansightCommand.Source
}

if ([string]::IsNullOrWhiteSpace($ReceiptPath) -and -not [string]::IsNullOrWhiteSpace($resolvedCliPath)) {
    try {
        $versionJson = (& $resolvedCliPath version --json 2>$null | Out-String).Trim()
        if (-not [string]::IsNullOrWhiteSpace($versionJson)) {
            $version = $versionJson | ConvertFrom-Json
            $ReceiptPath = [string]$version.receiptPath
        }
    }
    catch {
        # Fall through to the configured or default receipt path.
    }
}

if ([string]::IsNullOrWhiteSpace($InstallRoot) -and -not [string]::IsNullOrWhiteSpace($ReceiptPath)) {
    $InstallRoot = Split-Path -Parent $ReceiptPath
}
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $legacyInstallRoot = Join-Path $localApplicationData 'Ansight\cli'
    $legacyReceiptPath = Join-Path $legacyInstallRoot 'install.json'
    $InstallRoot = if (Test-Path -LiteralPath $legacyReceiptPath -PathType Leaf) {
        $legacyInstallRoot
    }
    else {
        Join-Path $localApplicationData 'Ansight\cli-install'
    }
}
if ([string]::IsNullOrWhiteSpace($ReceiptPath)) {
    $ReceiptPath = Join-Path $InstallRoot 'install.json'
}

$managedInstall = $false
$receipt = $null
if (Test-Path -LiteralPath $ReceiptPath -PathType Leaf) {
    try {
        $receipt = [IO.File]::ReadAllText($ReceiptPath) | ConvertFrom-Json
        if ([string]$receipt.schema -eq 'ansight.cli.installation/v1') {
            $receiptInstallRoot = [string]$receipt.installRoot
            $receiptDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $ReceiptPath))
            if (-not [string]::IsNullOrWhiteSpace($receiptInstallRoot) -and
                [IO.Path]::GetFullPath($receiptInstallRoot) -ieq $receiptDirectory) {
                $managedInstall = $true
                $InstallRoot = $receiptInstallRoot
                if ([string]::IsNullOrWhiteSpace($BinDirectory) -and
                    -not [string]::IsNullOrWhiteSpace([string]$receipt.binDirectory)) {
                    $BinDirectory = [string]$receipt.binDirectory
                }
            }
            else {
                Write-Warning "The receipt installRoot does not own $ReceiptPath; refusing managed file removal."
            }
        }
    }
    catch {
        Write-Warning "The installation receipt could not be read: $($_.Exception.Message)"
    }
}

if ([string]::IsNullOrWhiteSpace($BinDirectory)) {
    $BinDirectory = Join-Path $localApplicationData 'Ansight\bin'
}
if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
    $DataDirectory = Join-Path $localApplicationData 'Ansight\Cli'
}
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$BinDirectory = [IO.Path]::GetFullPath($BinDirectory)
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
if ([string]::IsNullOrWhiteSpace($resolvedCliPath)) {
    $installedWrapper = Join-Path $BinDirectory 'ansight.cmd'
    if (Test-Path -LiteralPath $installedWrapper -PathType Leaf) {
        $resolvedCliPath = $installedWrapper
    }
}

function Read-YesNo {
    param([Parameter(Mandatory = $true)][string]$Prompt)

    while ($true) {
        try {
            $answer = Read-Host "$Prompt [y/N]"
        }
        catch {
            return $false
        }

        if ([string]::IsNullOrWhiteSpace($answer) -or $answer -match '^(?i:n|no)$') {
            return $false
        }
        if ($answer -match '^(?i:y|yes)$') {
            return $true
        }
        Write-Host 'Please enter y or n.'
    }
}

function Assert-SafeRemovalTarget {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $root = [IO.Path]::GetPathRoot($fullPath).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $profile = [IO.Path]::GetFullPath($userProfile).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([string]::IsNullOrWhiteSpace($fullPath) -or
        $fullPath -eq $root -or
        $fullPath -eq $profile) {
        throw "Refusing to remove unsafe $Label path: $Path"
    }
}

function Stop-ManagedAnsightHostProcess {
    $metadataPath = Join-Path $DataDirectory 'ansight-host.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        return
    }

    try {
        $metadata = [IO.File]::ReadAllText($metadataPath) | ConvertFrom-Json
        $processId = [int]$metadata.processId
        if ($processId -le 0) {
            return
        }

        $hostProcess = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -eq $hostProcess) {
            return
        }

        $processPath = $hostProcess.Path
        $installPrefix = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'
        if ([string]::IsNullOrWhiteSpace($processPath) -or
            -not [IO.Path]::GetFullPath($processPath).StartsWith(
                $installPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            Write-Warning "Refusing to stop PID $processId because its executable is outside $InstallRoot."
            return
        }

        Stop-Process -Id $processId -Force
        [void]$hostProcess.WaitForExit(10000)
        Write-Host "Stopped the installer-managed Ansight host process (PID $processId)."
    }
    catch {
        Write-Warning "The running Ansight host could not be stopped: $($_.Exception.Message)"
    }
}

if (-not [string]::IsNullOrWhiteSpace($resolvedCliPath) -and (Test-Path -LiteralPath $resolvedCliPath)) {
    Write-Host 'Stopping the Ansight host...'
    try {
        & $resolvedCliPath host stop --data-dir $DataDirectory *> $null
    }
    catch {
        # A missing or already stopped host does not block uninstall.
    }
    Stop-ManagedAnsightHostProcess

    if (-not $NoSignOut) {
        if ($LocalSignOut) {
            Write-Host 'Clearing the local Ansight account session...'
            $localSignOutSucceeded = $false
            try {
                & $resolvedCliPath auth logout --local --data-dir $DataDirectory
                $localSignOutSucceeded = $LASTEXITCODE -eq 0
            }
            catch {
                $localSignOutSucceeded = $false
            }
            if (-not $localSignOutSucceeded) {
                Write-Warning 'The local account session could not be cleared.'
            }
        }
        else {
            Write-Host 'Signing out of Ansight...'
            $remoteSignOutSucceeded = $false
            try {
                & $resolvedCliPath auth logout --data-dir $DataDirectory
                $remoteSignOutSucceeded = $LASTEXITCODE -eq 0
            }
            catch {
                $remoteSignOutSucceeded = $false
            }
            if (-not $remoteSignOutSucceeded) {
                Write-Warning 'Remote revocation was unavailable; clearing the local account session.'
                $localSignOutSucceeded = $false
                try {
                    & $resolvedCliPath auth logout --local --data-dir $DataDirectory
                    $localSignOutSucceeded = $LASTEXITCODE -eq 0
                }
                catch {
                    $localSignOutSucceeded = $false
                }
                if (-not $localSignOutSucceeded) {
                    Write-Warning 'The local account session could not be cleared.'
                }
            }
        }
    }
}
elseif (-not $NoSignOut) {
    Write-Warning 'The Ansight CLI was not found, so account sign-out could not be performed.'
}

if (-not $KeepStartup) {
    try {
        if ($null -ne (Get-ScheduledTask -TaskName 'Ansight Host' -ErrorAction SilentlyContinue)) {
            Stop-ScheduledTask -TaskName 'Ansight Host' -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName 'Ansight Host' -Confirm:$false
            Write-Host 'Removed the Ansight scheduled task.'
        }
    }
    catch {
        Write-Warning "The Ansight scheduled task could not be removed: $($_.Exception.Message)"
    }

    $startupCommand = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Ansight Host.cmd'
    if (Test-Path -LiteralPath $startupCommand -PathType Leaf) {
        Remove-Item -LiteralPath $startupCommand -Force
        Write-Host 'Removed the Ansight Startup entry.'
    }
}

if ($managedInstall) {
    Assert-SafeRemovalTarget -Path $InstallRoot -Label 'installation'
    $wrapperPath = Join-Path $BinDirectory 'ansight.cmd'
    if (Test-Path -LiteralPath $wrapperPath -PathType Leaf) {
        $wrapperContents = [IO.File]::ReadAllText($wrapperPath)
        if ($wrapperContents.IndexOf($InstallRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Remove-Item -LiteralPath $wrapperPath -Force
        }
        else {
            Write-Warning "Preserving $wrapperPath because it does not reference this Ansight installation."
        }
    }

    foreach ($artifact in @('versions', 'current', 'install.json', 'update-check.json')) {
        $artifactPath = Join-Path $InstallRoot $artifact
        if (Test-Path -LiteralPath $artifactPath) {
            Remove-Item -LiteralPath $artifactPath -Recurse -Force
        }
    }

    if ((Test-Path -LiteralPath $InstallRoot -PathType Container) -and
        $null -eq (Get-ChildItem -LiteralPath $InstallRoot -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $InstallRoot -Force
    }
    if ((Test-Path -LiteralPath $BinDirectory -PathType Container) -and
        $null -eq (Get-ChildItem -LiteralPath $BinDirectory -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $BinDirectory -Force
    }
    Write-Host 'Removed the installer-managed Ansight CLI files.'
}
else {
    Write-Warning "No valid Ansight installation receipt was found at $ReceiptPath; no binary files were deleted."
}

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($managedInstall -and -not $KeepPath -and -not [string]::IsNullOrWhiteSpace($userPath)) {
    $updatedPathParts = @($userPath -split ';' | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        $_.TrimEnd('\') -ine $BinDirectory.TrimEnd('\')
    })
    $updatedPath = $updatedPathParts -join ';'
    if ($updatedPath -ne $userPath) {
        [Environment]::SetEnvironmentVariable('Path', $updatedPath, 'User')
        Write-Host "Removed $BinDirectory from the user PATH."
    }
}

$shouldRemoveSkills = $RemoveSkills
if (-not $RemoveSkills -and -not $KeepSkills) {
    $shouldRemoveSkills = Read-YesNo -Prompt 'Remove the installed Ansight agent skills, including any local edits to them?'
}
if ($shouldRemoveSkills) {
    foreach ($skillsRoot in @(
        (Join-Path $userProfile '.agents\skills'),
        (Join-Path $userProfile '.cursor\skills'),
        (Join-Path $userProfile '.claude\skills'))) {
        foreach ($skill in @(
            'ansight-cli-setup',
            'ansight-app-inspection',
            'ansight-operate-live-app',
            'ansight-use-remote-app-tools',
            'ansight-investigate-session',
            'ansight-annotate-session',
            'ansight-assess-automation-readiness',
            'ansight-ui-testing'
        )) {
            $skillPath = Join-Path $skillsRoot $skill
            if (Test-Path -LiteralPath $skillPath -PathType Container) {
                Remove-Item -LiteralPath $skillPath -Recurse -Force
                Write-Host "Removed agent skill: $skillPath"
            }
        }
    }
}

$shouldPurgeData = $PurgeData
if (-not $PurgeData -and -not $KeepData) {
    $shouldPurgeData = Read-YesNo -Prompt "Permanently remove all Ansight CLI captures, logs, and configuration at $DataDirectory?"
}
if ($shouldPurgeData) {
    Assert-SafeRemovalTarget -Path $DataDirectory -Label 'working-data'
    if (Test-Path -LiteralPath $DataDirectory -PathType Container) {
        Remove-Item -LiteralPath $DataDirectory -Recurse -Force
        Write-Host "Removed Ansight CLI working data: $DataDirectory"
    }
}
else {
    Write-Host "Preserved Ansight CLI working data: $DataDirectory"
}

Write-Host 'Ansight CLI uninstall complete.'
