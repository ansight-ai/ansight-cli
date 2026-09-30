param(
    [string]$Configuration = "Release",
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Rid = "win-x64",
    [string]$Version = "",
    [string]$BuildNumber = "",
    [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path,
    [string]$PackageDir = "",
    [string]$CertificateThumbprint = "",
    [string]$CertificateStoreName = "My",
    [string]$SignToolPath = "",
    [switch]$RequireSigning
)

$ErrorActionPreference = "Stop"

function Resolve-SignToolPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) {
            throw "signtool.exe was not found at '$RequestedPath'."
        }

        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $windowsKitsBin = Join-Path $programFilesX86 "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $windowsKitsBin -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $windowsKitsBin -Directory |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($candidate) {
            return $candidate
        }
    }

    throw "signtool.exe was not found. Install the Windows SDK or pass -SignToolPath."
}

function Invoke-AuthenticodeSigning {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedSignToolPath,
        [Parameter(Mandatory = $true)]
        [string]$StoreName,
        [Parameter(Mandatory = $true)]
        [string]$Thumbprint
    )

    & $ResolvedSignToolPath sign `
        /s $StoreName `
        /sha1 $Thumbprint `
        /tr "http://timestamp.digicert.com" `
        /td sha256 `
        /fd sha256 `
        $FilePath
    if ($LASTEXITCODE -ne 0) {
        throw "Signing failed for $FilePath with exit code $LASTEXITCODE."
    }

    & $ResolvedSignToolPath verify /pa /all /v $FilePath
    if ($LASTEXITCODE -ne 0) {
        throw "Signature verification failed for $FilePath with exit code $LASTEXITCODE."
    }
}

$versionFile = Join-Path $RepoRoot "ansight.version.props"
$cliProject = if ($env:PROJECT) { $env:PROJECT } else { Join-Path $RepoRoot "src\Ansight.Cli\Ansight.Cli.csproj" }
$trayProject = Join-Path $RepoRoot "src\Ansight.Tray.Windows\Ansight.Tray.Windows.csproj"
$appInspectionSkill = Join-Path $RepoRoot "skills\agents\ansight-app-inspection.md"
$agentSkillsRoot = Join-Path $RepoRoot "skills\agents"
$cliSetupSkill = Join-Path $RepoRoot "skills\ansight-cli-setup.md"
$coreAgentSkills = @(
    'ansight-annotate-session',
    'ansight-assess-automation-readiness',
    'ansight-investigate-session',
    'ansight-operate-live-app',
    'ansight-ui-testing',
    'ansight-use-remote-app-tools'
)

if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
    throw "Release metadata was not found: $versionFile"
}

$versionFileXml = [xml](Get-Content -LiteralPath $versionFile -Raw)
if ([string]::IsNullOrWhiteSpace($Version)) {
    $versionNode = $versionFileXml.SelectSingleNode("/Project/PropertyGroup/AnsightReleaseVersion")
    $Version = if ($versionNode) { $versionNode.InnerText.Trim() } else { "" }
}
if ([string]::IsNullOrWhiteSpace($BuildNumber)) {
    $buildNumberNode = $versionFileXml.SelectSingleNode("/Project/PropertyGroup/AnsightReleaseBuildNumber")
    $BuildNumber = if ($buildNumberNode) { $buildNumberNode.InnerText.Trim() } else { "" }
}

if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') {
    throw "Invalid CLI version '$Version'."
}
[long]$parsedBuildNumber = 0
if (-not [long]::TryParse($BuildNumber, [ref]$parsedBuildNumber) -or $parsedBuildNumber -le 0) {
    throw "CLI BuildNumber must be a positive integer: '$BuildNumber'."
}
$BuildNumber = $parsedBuildNumber.ToString([Globalization.CultureInfo]::InvariantCulture)

$CertificateThumbprint = $CertificateThumbprint.Replace(" ", "").ToUpperInvariant()
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -and
    $CertificateThumbprint -notmatch '^[0-9A-F]{40}$') {
    throw "CertificateThumbprint must contain exactly 40 hexadecimal characters."
}
if ($RequireSigning -and [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw "RequireSigning needs a CertificateThumbprint."
}
$resolvedSignToolPath = if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    ""
} else {
    Resolve-SignToolPath -RequestedPath $SignToolPath
}

foreach ($requiredFile in @($cliProject, $trayProject, $appInspectionSkill, $cliSetupSkill)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required CLI packaging input was not found: $requiredFile"
    }
}
foreach ($skill in $coreAgentSkills) {
    $skillEntrypoint = Join-Path (Join-Path $agentSkillsRoot $skill) 'SKILL.md'
    if (-not (Test-Path -LiteralPath $skillEntrypoint -PathType Leaf)) {
        throw "Required core agent skill was not found: $skillEntrypoint"
    }
}

if ([string]::IsNullOrWhiteSpace($PackageDir)) {
    $PackageDir = Join-Path $RepoRoot "products\cli\packages"
} elseif (-not [IO.Path]::IsPathRooted($PackageDir)) {
    $PackageDir = Join-Path $RepoRoot $PackageDir
}
$PackageDir = [IO.Path]::GetFullPath($PackageDir)
New-Item -ItemType Directory -Path $PackageDir -Force | Out-Null

$commitSha = $env:COMMIT_SHA
if ([string]::IsNullOrWhiteSpace($commitSha)) {
    $commitSha = (& git -C $RepoRoot rev-parse HEAD 2>$null | Select-Object -First 1)
}
if ([string]::IsNullOrWhiteSpace($commitSha)) {
    $commitSha = "unknown"
}

$stagingDirectory = Join-Path ([IO.Path]::GetTempPath()) "ansight-cli-$Rid-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

try {
    $restoreArguments = @()
    if ($env:NUGET_CONFIG_FILE) { $restoreArguments += "-p:RestoreConfigFile=$($env:NUGET_CONFIG_FILE)" }
    & dotnet publish $cliProject `
        -c $Configuration `
        -r $Rid `
        --self-contained true `
        "-p:PublishSingleFile=true" `
        "-p:AnsightCliVersion=$Version" `
        "-p:AnsightCliBuildNumber=$BuildNumber" `
        "-p:AnsightCommitSha=$commitSha" `
        "-p:IncludeNativeLibrariesForSelfExtract=true" `
        "-p:PublishTrimmed=false" `
        "-p:DebugType=none" `
        "-p:DebugSymbols=false" `
        -o $stagingDirectory @restoreArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Ansight CLI publish failed with exit code $LASTEXITCODE."
    }

    if ($env:ANSIGHT_STATIC_CLOUD_APP -eq 'true') {
        Remove-Item -LiteralPath (Join-Path $stagingDirectory 'ansight.exe') -Force -ErrorAction SilentlyContinue
        Move-Item -LiteralPath (Join-Path $stagingDirectory 'Ansight.Cloud.App.exe') -Destination (Join-Path $stagingDirectory 'ansight.exe') -Force
    }
    $cliExecutable = Join-Path $stagingDirectory "ansight.exe"
    if (-not (Test-Path -LiteralPath $cliExecutable -PathType Leaf)) {
        throw "Expected CLI executable was not produced: $cliExecutable"
    }

    foreach ($transitivePublishArtifact in @(
        "libAnsightSimulatorHid.dylib",
        "libAnsightSimulatorRtc.dylib"
    )) {
        $transitivePublishPath = Join-Path $stagingDirectory $transitivePublishArtifact
        if (Test-Path -LiteralPath $transitivePublishPath -PathType Leaf) {
            Remove-Item -LiteralPath $transitivePublishPath -Force
        }
    }

    $trayDirectory = Join-Path ([IO.Path]::GetTempPath()) "ansight-tray-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $trayDirectory -Force | Out-Null
    try {
        & dotnet publish $trayProject `
            -c $Configuration `
            -r $Rid `
            --self-contained true `
            "-p:PublishSingleFile=true" `
            "-p:PublishTrimmed=false" `
            "-p:DebugType=none" `
            "-p:DebugSymbols=false" `
            -o $trayDirectory @restoreArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Ansight Windows tray publish failed with exit code $LASTEXITCODE."
        }

        $trayExecutable = Join-Path $trayDirectory "ansight-tray.exe"
        if (-not (Test-Path -LiteralPath $trayExecutable -PathType Leaf)) {
            throw "Expected tray executable was not produced: $trayExecutable"
        }
        Copy-Item -LiteralPath $trayExecutable -Destination (Join-Path $stagingDirectory "ansight-tray.exe")
    }
    finally {
        if (Test-Path -LiteralPath $trayDirectory -PathType Container) {
            Remove-Item -LiteralPath $trayDirectory -Recurse -Force
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($resolvedSignToolPath)) {
        Invoke-AuthenticodeSigning `
            -FilePath $cliExecutable `
            -ResolvedSignToolPath $resolvedSignToolPath `
            -StoreName $CertificateStoreName `
            -Thumbprint $CertificateThumbprint
        Invoke-AuthenticodeSigning `
            -FilePath (Join-Path $stagingDirectory "ansight-tray.exe") `
            -ResolvedSignToolPath $resolvedSignToolPath `
            -StoreName $CertificateStoreName `
            -Thumbprint $CertificateThumbprint
    } elseif ($RequireSigning) {
        throw "Windows CLI signing was required but no signing configuration was provided."
    } else {
        Write-Warning "Windows CLI executables are unsigned. Pass -CertificateThumbprint or use -RequireSigning for release builds."
    }

    Copy-Item -LiteralPath (Join-Path $RepoRoot "LICENSE") -Destination (Join-Path $stagingDirectory "LICENSE")
    Copy-Item -LiteralPath (Join-Path $RepoRoot "NOTICE") -Destination (Join-Path $stagingDirectory "NOTICE")

    $appInspectionSkillDirectory = Join-Path $stagingDirectory "skills\ansight-app-inspection"
    $cliSetupSkillDirectory = Join-Path $stagingDirectory "skills\ansight-cli-setup"
    New-Item -ItemType Directory -Path $appInspectionSkillDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $cliSetupSkillDirectory -Force | Out-Null
    Copy-Item -LiteralPath $appInspectionSkill -Destination (Join-Path $appInspectionSkillDirectory "SKILL.md")
    Copy-Item -LiteralPath $cliSetupSkill -Destination (Join-Path $cliSetupSkillDirectory "SKILL.md")
    foreach ($skill in $coreAgentSkills) {
        $sourceSkillDirectory = Join-Path $agentSkillsRoot $skill
        $destinationSkillDirectory = Join-Path (Join-Path $stagingDirectory 'skills') $skill
        New-Item -ItemType Directory -Path $destinationSkillDirectory -Force | Out-Null
        Copy-Item -Path (Join-Path $sourceSkillDirectory '*') -Destination $destinationSkillDirectory -Recurse -Force
    }

    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if ($null -eq $tar) {
        throw "tar.exe is required to create the CLI archive."
    }

    $archiveName = "ansight-cli-$Rid-$Version-$BuildNumber.tar.gz"
    $archivePath = Join-Path $PackageDir $archiveName
    & $tar.Source -czf $archivePath -C $stagingDirectory .
    if ($LASTEXITCODE -ne 0) {
        throw "CLI archive creation failed with exit code $LASTEXITCODE."
    }

    $checksumPath = "$archivePath.sha256"
    $sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        $checksumPath,
        "$sha256  $archiveName`r`n",
        [Text.Encoding]::ASCII)

    Write-Output $archivePath
    Write-Output $checksumPath
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory -PathType Container) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
