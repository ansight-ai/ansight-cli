param(
    [string]$Version = $env:ANSIGHT_INSTALL_VERSION,
    [string]$BuildNumber = $env:ANSIGHT_INSTALL_BUILD_NUMBER,
    [string]$Channel = $env:ANSIGHT_INSTALL_CHANNEL,
    [string]$InstallRoot = $env:ANSIGHT_INSTALL_ROOT,
    [string]$BinDirectory = $env:ANSIGHT_BIN_DIR,
    [string]$ReleaseUrl = $env:ANSIGHT_RELEASE_URL,
    [string]$DownloadBaseUrl = $env:ANSIGHT_DOWNLOAD_BASE_URL,
    [string]$PortalUrl = $env:ANSIGHT_PORTAL_URL,
    [switch]$Yes,
    [switch]$NoSetup,
    [switch]$NoPathUpdate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:SkillInstallNoticeShown = $false
Add-Type -AssemblyName System.Net.Http

$userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)

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
if ([string]::IsNullOrWhiteSpace($BinDirectory)) {
    $BinDirectory = Join-Path $localApplicationData 'Ansight\bin'
}
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$BinDirectory = [IO.Path]::GetFullPath($BinDirectory)
$installationEvent = if (Test-Path -LiteralPath (Join-Path $InstallRoot 'install.json') -PathType Leaf) {
    'update'
}
else {
    'install'
}
if ([string]::IsNullOrWhiteSpace($Channel)) {
    $Channel = if ($ReleaseUrl -match '/preview/' -or $DownloadBaseUrl -match '/preview/?$') {
        'preview'
    }
    else {
        'public'
    }
}
$Channel = $Channel.Trim().ToLowerInvariant()
if ($Channel -notin @('public', 'preview')) {
    throw "Channel must be 'public' or 'preview'."
}
if ([string]::IsNullOrWhiteSpace($ReleaseUrl)) {
    $ReleaseUrl = if ($Channel -eq 'preview') {
        'https://www.ansight.ai/preview/release.json'
    }
    else {
        'https://www.ansight.ai/release.json'
    }
}
if ([string]::IsNullOrWhiteSpace($DownloadBaseUrl)) {
    $DownloadBaseUrl = if ($Channel -eq 'preview') {
        'https://ansightaus.blob.core.windows.net/builds/cli/preview'
    }
    else {
        'https://ansightaus.blob.core.windows.net/builds/cli'
    }
}
if ([string]::IsNullOrWhiteSpace($PortalUrl)) {
    $PortalUrl = 'https://app.ansight.ai/?source=cli'
    if ($env:ANSIGHT_ACQUISITION_ID -match '^[0-9a-fA-F-]{36}$') {
        $PortalUrl += '&journey_id=' + $env:ANSIGHT_ACQUISITION_ID
    }
}

if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'This installer supports Windows. On macOS or Linux, use install.sh.'
}

$osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if ($osArchitecture -eq [Runtime.InteropServices.Architecture]::X64) {
    $architecture = 'x64'
}
elseif ($osArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) {
    $architecture = 'arm64'
}
else {
    throw "Unsupported CPU architecture '$osArchitecture'."
}
$rid = "win-$architecture"

function Invoke-AnsightDownload {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromMinutes(30)
    $response = $null
    $sourceStream = $null
    $destinationStream = $null
    try {
        [Console]::Error.WriteLine("Downloading {0}..." -f $Label)
        $response = $client.GetAsync(
            $Uri,
            [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $null = $response.EnsureSuccessStatusCode()
        $totalBytes = $response.Content.Headers.ContentLength
        $sourceStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $destinationStream = [IO.File]::Create($Destination)
        $buffer = [byte[]]::new(65536)
        [long]$receivedBytes = 0
        [int]$lastReportedPercent = -1
        [long]$nextUnboundedReport = 1048576

        while (($read = $sourceStream.ReadAsync($buffer, 0, $buffer.Length).GetAwaiter().GetResult()) -gt 0) {
            $destinationStream.Write($buffer, 0, $read)
            $receivedBytes += $read
            if ($null -ne $totalBytes -and $totalBytes -gt 0) {
                $percent = [Math]::Min(100, [int][Math]::Floor(($receivedBytes * 100.0) / $totalBytes))
                if ($percent -ne $lastReportedPercent) {
                    [Console]::Error.Write((
                        "`rDownloading {0}: {1}% ({2}/{3} bytes)" -f
                            $Label,
                            $percent,
                            $receivedBytes,
                            $totalBytes))
                    $lastReportedPercent = $percent
                }
            }
            elseif ($receivedBytes -ge $nextUnboundedReport) {
                [Console]::Error.Write((
                    "`rDownloading {0}: {1} bytes" -f $Label, $receivedBytes))
                $nextUnboundedReport = $receivedBytes + 1048576
            }
        }

        $destinationStream.Flush()
        if ($null -ne $totalBytes -and $totalBytes -gt 0) {
            [Console]::Error.WriteLine((
                "`rDownloading {0}: 100% ({1}/{2} bytes)" -f
                    $Label,
                    $receivedBytes,
                    $totalBytes))
        }
        else {
            [Console]::Error.WriteLine((
                "`rDownloaded {0}: {1} bytes" -f $Label, $receivedBytes))
        }
    }
    finally {
        if ($null -ne $destinationStream) {
            $destinationStream.Dispose()
        }
        if ($null -ne $sourceStream) {
            $sourceStream.Dispose()
        }
        if ($null -ne $response) {
            $response.Dispose()
        }
        $client.Dispose()
    }
}

function Read-YesNo {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [bool]$DefaultYes = $false
    )

    if ($Yes) {
        return $true
    }

    $suffix = if ($DefaultYes) { 'Y/n' } else { 'y/N' }
    while ($true) {
        try {
            $answer = Read-Host "$Prompt [$suffix]"
        }
        catch {
            Write-Host 'No interactive terminal is available; skipping optional setup.'
            return $false
        }

        if ([string]::IsNullOrWhiteSpace($answer)) {
            return $DefaultYes
        }
        if ($answer -match '^(?i:y|yes)$') {
            return $true
        }
        if ($answer -match '^(?i:n|no)$') {
            return $false
        }
        Write-Host 'Please enter y or n.'
    }
}

function Install-AnsightSkill {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$DestinationRoot,
        [bool]$DefaultYes = $false
    )

    if (-not (Test-Path -LiteralPath $script:SkillsRoot -PathType Container)) {
        return
    }

    $skills = @(
        'ansight-cli-setup',
        'ansight-app-inspection',
        'ansight-operate-live-app',
        'ansight-use-remote-app-tools',
        'ansight-investigate-session',
        'ansight-annotate-session',
        'ansight-assess-automation-readiness',
        'ansight-ui-testing'
    )
    $skillsAreCurrent = $true
    $sourceCount = 0
    foreach ($skill in $skills) {
        $source = Join-Path $script:SkillsRoot $skill
        if (-not (Test-Path -LiteralPath (Join-Path $source 'SKILL.md') -PathType Leaf)) {
            continue
        }

        $sourceCount++
        $destination = Join-Path $DestinationRoot $skill
        $sourcePrefix = $source.TrimEnd('\') + '\'
        foreach ($sourceFile in Get-ChildItem -LiteralPath $source -File -Recurse) {
            $relativePath = $sourceFile.FullName.Substring($sourcePrefix.Length)
            $destinationFile = Join-Path $destination $relativePath
            if (-not (Test-Path -LiteralPath $destinationFile -PathType Leaf) -or
                (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $destinationFile -Algorithm SHA256).Hash) {
                $skillsAreCurrent = $false
                break
            }
        }
        if (-not $skillsAreCurrent) {
            break
        }
    }

    if ($sourceCount -eq $skills.Count -and $skillsAreCurrent) {
        Write-Host "Ansight agent skills for $Label are already current."
        return
    }

    if (-not $script:SkillInstallNoticeShown) {
        Write-Host 'Ansight agent skills are necessary for your agent to work fully and provide the best experience with Ansight.'
        Write-Host 'Review all skill instructions and supporting files: https://www.ansight.ai/skills'
        Write-Host 'Release manifests and verification: https://www.ansight.ai/docs/skills/disclosure'
        Write-Host "This release's skill manifest (when available): ${archiveUrl}.skills.json"
        Write-Host 'Installation is optional, but the experience may be degraded without them.'
        $script:SkillInstallNoticeShown = $true
    }

    if (-not (Read-YesNo -Prompt "Install the Ansight agent skills for $Label?" -DefaultYes $DefaultYes)) {
        return
    }

    foreach ($skill in $skills) {
        $source = Join-Path $script:SkillsRoot $skill
        if (-not (Test-Path -LiteralPath (Join-Path $source 'SKILL.md') -PathType Leaf)) {
            continue
        }

        $destination = Join-Path $DestinationRoot $skill
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
        Write-Host "Installed $Label skill: $(Join-Path $destination 'SKILL.md')"
    }
}

function Test-AnsightCommandOrDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string]$Directory
    )

    return $null -ne (Get-Command $Command -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $Directory)
}

function Enable-AnsightStartup {
    param(
        [Parameter(Mandatory = $true)][string]$AnsightExecutable,
        [Parameter(Mandatory = $true)][string]$AnsightWrapper
    )

    $taskName = 'Ansight Host'
    & $AnsightExecutable host status --json *> $null
    $alreadyRunning = $LASTEXITCODE -eq 0

    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $taskArguments = '/d /s /c ""{0}" host run"' -f $AnsightWrapper
        $action = New-ScheduledTaskAction `
            -Execute $env:ComSpec `
            -Argument $taskArguments `
            -WorkingDirectory (Split-Path -Parent $AnsightWrapper)
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
        $principal = New-ScheduledTaskPrincipal `
            -UserId $identity `
            -LogonType Interactive `
            -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet `
            -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries `
            -StartWhenAvailable
        Register-ScheduledTask `
            -TaskName $taskName `
            -Action $action `
            -Trigger $trigger `
            -Principal $principal `
            -Settings $settings `
            -Force | Out-Null

        if ($alreadyRunning) {
            Write-Host 'Enabled the Ansight logon task. The current host is already running.'
        }
        else {
            Start-ScheduledTask -TaskName $taskName
            Write-Host 'Enabled and started the Ansight host with Task Scheduler.'
        }
        return
    }
    catch {
        Write-Warning "Task Scheduler setup was unavailable: $($_.Exception.Message)"
    }

    $startupDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
    $startupCommand = Join-Path $startupDirectory 'Ansight Host.cmd'
    New-Item -ItemType Directory -Path $startupDirectory -Force | Out-Null
    $commandContents = "@echo off`r`nstart `"`" /min `"$AnsightWrapper`" host run`r`n"
    [IO.File]::WriteAllText($startupCommand, $commandContents, [Text.Encoding]::ASCII)
    Write-Host "Installed the Ansight Startup entry: $startupCommand"
}

$temporaryDirectory = $null
try {
    if ([string]::IsNullOrWhiteSpace($Version) -or [string]::IsNullOrWhiteSpace($BuildNumber)) {
        $release = Invoke-RestMethod -Uri $ReleaseUrl -Method Get
        $productProperty = $release.PSObject.Properties['product']
        if ($null -ne $productProperty -and
            -not [string]::IsNullOrWhiteSpace([string]$productProperty.Value) -and
            [string]$productProperty.Value -ne 'cli') {
            throw "The release feed describes '$($productProperty.Value)', not the Ansight CLI."
        }
        if ([string]::IsNullOrWhiteSpace($Version)) {
            $cliVersionProperty = $release.PSObject.Properties['cliVersion']
            $Version = if ($null -ne $cliVersionProperty -and
                -not [string]::IsNullOrWhiteSpace([string]$cliVersionProperty.Value)) {
                [string]$cliVersionProperty.Value
            }
            else {
                [string]$release.version
            }
        }
        if ([string]::IsNullOrWhiteSpace($BuildNumber)) {
            $cliBuildNumberProperty = $release.PSObject.Properties['cliBuildNumber']
            $BuildNumber = if ($null -ne $cliBuildNumberProperty -and
                -not [string]::IsNullOrWhiteSpace([string]$cliBuildNumberProperty.Value)) {
                [string]$cliBuildNumberProperty.Value
            }
            else {
                [string]$release.buildNumber
            }
        }
    }

    if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') {
        throw "Invalid release version '$Version'."
    }
    [long]$parsedBuildNumber = 0
    if (-not [long]::TryParse($BuildNumber, [ref]$parsedBuildNumber) -or $parsedBuildNumber -le 0) {
        throw 'The release manifest does not contain a positive integer buildNumber.'
    }
    $BuildNumber = $parsedBuildNumber.ToString([Globalization.CultureInfo]::InvariantCulture)

    $archiveName = "ansight-cli-$rid-$Version-$BuildNumber.tar.gz"
    $archiveUrl = "$($DownloadBaseUrl.TrimEnd('/'))/$rid/$Version/$BuildNumber/$archiveName"
    $checksumUrl = "$archiveUrl.sha256"
    $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "ansight-install-$([Guid]::NewGuid().ToString('N'))"
    $archivePath = Join-Path $temporaryDirectory $archiveName
    $checksumPath = "$archivePath.sha256"
    $payloadDirectory = Join-Path $temporaryDirectory 'payload'
    New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null

    Write-Host "Installing Ansight CLI $Version ($BuildNumber) for $rid from $Channel..."
    Invoke-AnsightDownload -Uri $checksumUrl -Destination $checksumPath -Label 'release checksum'
    Invoke-AnsightDownload -Uri $archiveUrl -Destination $archivePath -Label 'CLI archive'

    Write-Host 'Verifying the CLI archive...'
    $checksumText = [IO.File]::ReadAllText($checksumPath)
    $checksumMatch = [Text.RegularExpressions.Regex]::Match($checksumText, '(?i)\b[0-9a-f]{64}\b')
    if (-not $checksumMatch.Success) {
        throw 'The downloaded checksum file is invalid.'
    }

    $expectedSha256 = $checksumMatch.Value.ToLowerInvariant()
    $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        throw "Checksum mismatch for $archiveName. Expected $expectedSha256, got $actualSha256."
    }
    Write-Host "SHA-256 verified: $actualSha256"

    Write-Host 'Inspecting and extracting the CLI archive...'
    $tarCommand = Get-Command tar.exe -ErrorAction SilentlyContinue
    if ($null -eq $tarCommand) {
        throw 'tar.exe is required to extract the CLI archive.'
    }

    $archiveEntries = & $tarCommand.Source -tzf $archivePath
    if ($LASTEXITCODE -ne 0) {
        throw 'The CLI archive could not be inspected.'
    }
    foreach ($entry in $archiveEntries) {
        if ($entry.StartsWith('/') -or
            $entry.StartsWith('../') -or
            $entry.Contains('/../') -or
            $entry -match '^[A-Za-z]:[\\/]') {
            throw "The CLI archive contains an unsafe path: $entry"
        }
    }

    & $tarCommand.Source -xzf $archivePath -C $payloadDirectory
    if ($LASTEXITCODE -ne 0) {
        throw 'The CLI archive could not be extracted.'
    }

    $payloadExecutable = Join-Path $payloadDirectory 'ansight.exe'
    if (-not (Test-Path -LiteralPath $payloadExecutable -PathType Leaf)) {
        throw 'The CLI archive does not contain ansight.exe.'
    }
    Write-Host 'Validating the downloaded CLI...'
    & $payloadExecutable help *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'The downloaded Ansight CLI did not start successfully.'
    }

    $versionsDirectory = Join-Path $InstallRoot 'versions'
    $versionDirectory = Join-Path $versionsDirectory "$Version-$BuildNumber-$rid"
    $pendingDirectory = Join-Path $versionsDirectory ".install-$Version-$BuildNumber-$rid-$PID"
    $checksumMarker = Join-Path $versionDirectory '.archive.sha256'
    New-Item -ItemType Directory -Path $versionsDirectory -Force | Out-Null

    $reuseExisting = (Test-Path -LiteralPath $checksumMarker -PathType Leaf) -and
        ([IO.File]::ReadAllText($checksumMarker).Trim() -eq $actualSha256)
    if (-not $reuseExisting) {
        if (Test-Path -LiteralPath $pendingDirectory) {
            Remove-Item -LiteralPath $pendingDirectory -Recurse -Force
        }
        New-Item -ItemType Directory -Path $pendingDirectory -Force | Out-Null
        Copy-Item -Path (Join-Path $payloadDirectory '*') -Destination $pendingDirectory -Recurse -Force
        [IO.File]::WriteAllText(
            (Join-Path $pendingDirectory '.archive.sha256'),
            "$actualSha256`r`n",
            [Text.Encoding]::ASCII)

        if (Test-Path -LiteralPath $versionDirectory) {
            Remove-Item -LiteralPath $versionDirectory -Recurse -Force
        }
        Move-Item -LiteralPath $pendingDirectory -Destination $versionDirectory
    }

    $ansightExecutable = Join-Path $versionDirectory 'ansight.exe'
    Write-Host 'Activating the downloaded CLI...'
    New-Item -ItemType Directory -Path $BinDirectory -Force | Out-Null
    $wrapperPath = Join-Path $BinDirectory 'ansight.cmd'
    $pendingWrapperPath = "$wrapperPath.new"
    $wrapperContents = "@echo off`r`n`"$ansightExecutable`" %*`r`n"
    [IO.File]::WriteAllText($pendingWrapperPath, $wrapperContents, [Text.Encoding]::ASCII)
    Move-Item -LiteralPath $pendingWrapperPath -Destination $wrapperPath -Force

    $receiptPath = Join-Path $InstallRoot 'install.json'
    $pendingReceiptPath = "$receiptPath.new-$PID"
    $receipt = [ordered]@{
        schema = 'ansight.cli.installation/v1'
        version = $Version
        buildNumber = $parsedBuildNumber
        channel = $Channel
        rid = $rid
        releaseUrl = $ReleaseUrl
        downloadBaseUrl = $DownloadBaseUrl.TrimEnd('/')
        archiveUrl = $archiveUrl
        sha256 = $actualSha256
        installedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        installRoot = $InstallRoot
        binDirectory = $BinDirectory
    }
    [IO.File]::WriteAllText(
        $pendingReceiptPath,
        (($receipt | ConvertTo-Json -Depth 3) + "`r`n"),
        [Text.Encoding]::UTF8)
    Move-Item -LiteralPath $pendingReceiptPath -Destination $receiptPath -Force

    $pathParts = @($env:Path -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $binIsOnPath = $pathParts | Where-Object { $_.TrimEnd('\') -ieq $BinDirectory.TrimEnd('\') }
    if (-not $binIsOnPath) {
        if (-not $NoPathUpdate) {
            $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
            $userPathParts = @($userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            $userPathHasBin = $userPathParts | Where-Object { $_.TrimEnd('\') -ieq $BinDirectory.TrimEnd('\') }
            if (-not $userPathHasBin) {
                $newUserPath = (@($BinDirectory) + $userPathParts) -join ';'
                [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
                Write-Host "Added $BinDirectory to your user PATH."
            }
        }
        else {
            Write-Host "Add $BinDirectory to your user PATH."
        }
        $env:Path = "$BinDirectory;$env:Path"
    }

    & $ansightExecutable help *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'The installed Ansight CLI did not start successfully.'
    }

    $script:SkillsRoot = Join-Path $versionDirectory 'skills'

    try {
        & $ansightExecutable analytics lifecycle $installationEvent `
            --channel $Channel --rid $rid --distribution powershell --silent *> $null
    }
    catch { # Measurement must never fail an installation.
    }

    if (-not $NoSetup) {
        $codexDirectory = Join-Path $userProfile '.codex'
        $cursorDirectory = Join-Path $userProfile '.cursor'
        $claudeDirectory = Join-Path $userProfile '.claude'
        Install-AnsightSkill `
            -Label 'Codex and shared Agent Skills clients' `
            -DestinationRoot (Join-Path $userProfile '.agents\skills') `
            -DefaultYes (Test-AnsightCommandOrDirectory -Command 'codex' -Directory $codexDirectory)
        Install-AnsightSkill `
            -Label 'Cursor' `
            -DestinationRoot (Join-Path $cursorDirectory 'skills') `
            -DefaultYes (Test-AnsightCommandOrDirectory -Command 'cursor' -Directory $cursorDirectory)
        Install-AnsightSkill `
            -Label 'Claude Code' `
            -DestinationRoot (Join-Path $claudeDirectory 'skills') `
            -DefaultYes (Test-AnsightCommandOrDirectory -Command 'claude' -Directory $claudeDirectory)

        Write-Host 'Local developer tools are free and require no Ansight account.'
        Write-Host 'For cloud sharing, uploads or delegated jobs, run: ansight account login'
        if (Read-YesNo -Prompt 'Start the Ansight host automatically on this computer?' -DefaultYes $true) {
            Enable-AnsightStartup `
                -AnsightExecutable $ansightExecutable `
                -AnsightWrapper $wrapperPath
        }
        else {
            Write-Host 'Start the host when needed with: ansight host run'
        }
    }

    Write-Host ''
    Write-Host "Ansight CLI $Version ($BuildNumber) is installed from $Channel."
    Write-Host "Command: $wrapperPath"
    Write-Host "Payload: $versionDirectory"
    Write-Host 'Update: irm https://www.ansight.ai/update.ps1 | iex'
    Write-Host 'Uninstall: irm https://www.ansight.ai/uninstall.ps1 | iex'
    if (-not $binIsOnPath) {
        Write-Host 'Open a new terminal to use ansight from your updated PATH.'
    }
}
catch {
    throw
}
finally {
    if ($null -ne $temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory)) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
