param(
    [string]$Version = $env:ANSIGHT_UPDATE_VERSION,
    [string]$BuildNumber = $env:ANSIGHT_UPDATE_BUILD_NUMBER,
    [string]$Channel = $env:ANSIGHT_UPDATE_CHANNEL,
    [string]$ReceiptPath = $env:ANSIGHT_INSTALL_RECEIPT,
    [string]$CliPath = $env:ANSIGHT_UPDATE_CLI,
    [string]$ReleaseUrl = $env:ANSIGHT_RELEASE_URL,
    [string]$DownloadBaseUrl = $env:ANSIGHT_DOWNLOAD_BASE_URL,
    [string]$InstallerUrl = $env:ANSIGHT_INSTALLER_URL,
    [switch]$Check,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:ANSIGHT_NO_UPDATE_CHECK = '1'
$downloadBaseUrlWasOverridden = $PSBoundParameters.ContainsKey('DownloadBaseUrl') -or
    -not [string]::IsNullOrWhiteSpace($env:ANSIGHT_DOWNLOAD_BASE_URL)

function Get-JsonStringProperty {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return ''
    }
    return [string]$property.Value
}

if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'This updater supports Windows. On macOS or Linux, use update.sh.'
}

$localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$resolvedCliPath = $CliPath
if ([string]::IsNullOrWhiteSpace($resolvedCliPath)) {
    $ansightCommand = Get-Command ansight -ErrorAction SilentlyContinue
    if ($null -ne $ansightCommand) {
        $resolvedCliPath = $ansightCommand.Source
    }
}

if ([string]::IsNullOrWhiteSpace($ReceiptPath) -and -not [string]::IsNullOrWhiteSpace($resolvedCliPath)) {
    try {
        $versionJson = (& $resolvedCliPath version --json 2>$null | Out-String).Trim()
        if (-not [string]::IsNullOrWhiteSpace($versionJson)) {
            $installedVersion = $versionJson | ConvertFrom-Json
            $ReceiptPath = [string]$installedVersion.receiptPath
        }
    }
    catch {
        # Fall through to the configured or default receipt paths.
    }
}

if ([string]::IsNullOrWhiteSpace($ReceiptPath)) {
    $newReceiptPath = Join-Path $localApplicationData 'Ansight\cli-install\install.json'
    $legacyReceiptPath = Join-Path $localApplicationData 'Ansight\cli\install.json'
    $ReceiptPath = if (Test-Path -LiteralPath $newReceiptPath -PathType Leaf) {
        $newReceiptPath
    }
    else {
        $legacyReceiptPath
    }
}
if (-not (Test-Path -LiteralPath $ReceiptPath -PathType Leaf)) {
    throw "No installer-managed CLI receipt was found at $ReceiptPath. Run install.ps1 first or pass -ReceiptPath."
}
$ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)

$receipt = [IO.File]::ReadAllText($ReceiptPath) | ConvertFrom-Json
if ((Get-JsonStringProperty -Object $receipt -Name 'schema') -ne 'ansight.cli.installation/v1') {
    throw "$ReceiptPath is not an Ansight CLI installation receipt."
}

$currentVersion = Get-JsonStringProperty -Object $receipt -Name 'version'
[long]$currentBuildNumber = 0
if ([string]::IsNullOrWhiteSpace($currentVersion) -or
    -not [long]::TryParse(
        (Get-JsonStringProperty -Object $receipt -Name 'buildNumber'),
        [ref]$currentBuildNumber) -or
    $currentBuildNumber -le 0) {
    throw 'The installation receipt has an invalid version or buildNumber.'
}
$currentChannel = (Get-JsonStringProperty -Object $receipt -Name 'channel').Trim().ToLowerInvariant()
if ($currentChannel -notin @('public', 'preview')) {
    throw "The installation receipt has an invalid channel '$currentChannel'."
}
$installRoot = Get-JsonStringProperty -Object $receipt -Name 'installRoot'
$binDirectory = Get-JsonStringProperty -Object $receipt -Name 'binDirectory'
if ([string]::IsNullOrWhiteSpace($installRoot) -or [string]::IsNullOrWhiteSpace($binDirectory)) {
    throw 'The installation receipt is missing installRoot or binDirectory.'
}
$installRoot = [IO.Path]::GetFullPath($installRoot)
$binDirectory = [IO.Path]::GetFullPath($binDirectory)
$receiptDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $ReceiptPath))
if ([IO.Path]::GetFileName($ReceiptPath) -ine 'install.json' -or
    $installRoot -ine $receiptDirectory) {
    throw "The receipt installRoot does not own $ReceiptPath; refusing to update it."
}

if ([string]::IsNullOrWhiteSpace($Channel)) {
    $Channel = $currentChannel
}
$Channel = $Channel.Trim().ToLowerInvariant()
if ($Channel -notin @('public', 'preview')) {
    throw "Channel must be 'public' or 'preview'."
}

$defaultReleaseUrl = if ($Channel -eq 'preview') {
    'https://www.ansight.ai/preview/release.json'
}
else {
    'https://www.ansight.ai/release.json'
}
$defaultDownloadBaseUrl = if ($Channel -eq 'preview') {
    'https://ansightaus.blob.core.windows.net/builds/cli/preview'
}
else {
    'https://ansightaus.blob.core.windows.net/builds/cli'
}
if ([string]::IsNullOrWhiteSpace($ReleaseUrl)) {
    $receiptReleaseUrl = Get-JsonStringProperty -Object $receipt -Name 'releaseUrl'
    $ReleaseUrl = if ($Channel -eq $currentChannel -and
        -not [string]::IsNullOrWhiteSpace($receiptReleaseUrl)) {
        $receiptReleaseUrl
    }
    else {
        $defaultReleaseUrl
    }
}
if ([string]::IsNullOrWhiteSpace($DownloadBaseUrl)) {
    $receiptDownloadBaseUrl = Get-JsonStringProperty -Object $receipt -Name 'downloadBaseUrl'
    $DownloadBaseUrl = if ($Channel -eq $currentChannel -and
        -not [string]::IsNullOrWhiteSpace($receiptDownloadBaseUrl)) {
        $receiptDownloadBaseUrl
    }
    else {
        $defaultDownloadBaseUrl
    }
}
if (-not $downloadBaseUrlWasOverridden -and
    $ReleaseUrl.Equals($defaultReleaseUrl, [StringComparison]::OrdinalIgnoreCase) -and
    $DownloadBaseUrl.StartsWith('file:', [StringComparison]::OrdinalIgnoreCase)) {
    $DownloadBaseUrl = $defaultDownloadBaseUrl
}
$DownloadBaseUrl = $DownloadBaseUrl.TrimEnd('/')

if ([string]::IsNullOrWhiteSpace($InstallerUrl)) {
    $InstallerUrl = 'https://www.ansight.ai/install.ps1'
}
if ([string]::IsNullOrWhiteSpace($Version) -xor [string]::IsNullOrWhiteSpace($BuildNumber)) {
    throw 'Use -Version and -BuildNumber together when selecting an exact CLI build.'
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $release = Invoke-RestMethod -Uri $ReleaseUrl -Method Get
    $productProperty = $release.PSObject.Properties['product']
    if ($null -ne $productProperty -and
        -not [string]::IsNullOrWhiteSpace([string]$productProperty.Value) -and
        [string]$productProperty.Value -ne 'cli') {
        throw "The release feed describes '$($productProperty.Value)', not the Ansight CLI."
    }
    $feedChannel = (Get-JsonStringProperty -Object $release -Name 'channel').Trim().ToLowerInvariant()
    if (-not [string]::IsNullOrWhiteSpace($feedChannel) -and $feedChannel -ne $Channel) {
        throw "The $Channel release feed identified itself as '$feedChannel'."
    }
    $Version = Get-JsonStringProperty -Object $release -Name 'cliVersion'
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = Get-JsonStringProperty -Object $release -Name 'version'
    }
    $BuildNumber = Get-JsonStringProperty -Object $release -Name 'cliBuildNumber'
    if ([string]::IsNullOrWhiteSpace($BuildNumber)) {
        $BuildNumber = Get-JsonStringProperty -Object $release -Name 'buildNumber'
    }
}

if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') {
    throw "Invalid target version '$Version'."
}
[long]$targetBuildNumber = 0
if (-not [long]::TryParse($BuildNumber, [ref]$targetBuildNumber) -or $targetBuildNumber -le 0) {
    throw 'The selected release has no positive integer buildNumber.'
}
$BuildNumber = $targetBuildNumber.ToString([Globalization.CultureInfo]::InvariantCulture)

Write-Host "Current: Ansight CLI $currentVersion ($currentBuildNumber) [$currentChannel]"
Write-Host "Target:  Ansight CLI $Version ($BuildNumber) [$Channel]"

$isCurrent = $currentVersion -eq $Version -and
    $currentBuildNumber -eq $targetBuildNumber -and
    $currentChannel -eq $Channel
if ($Check) {
    if ($isCurrent) {
        Write-Host 'Ansight CLI is current.'
    }
    else {
        Write-Host 'An Ansight CLI update or channel change is available.'
    }
    return
}
if ($isCurrent -and -not $Force) {
    Write-Host 'Ansight CLI is already current.'
    return
}

$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "ansight-update-$([Guid]::NewGuid().ToString('N'))"
$installerPath = Join-Path $temporaryDirectory 'install.ps1'
try {
    New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
    Invoke-WebRequest -Uri $InstallerUrl -OutFile $installerPath -UseBasicParsing
    $powerShellPath = (Get-Process -Id $PID).Path
    & $powerShellPath `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $installerPath `
        -Version $Version `
        -BuildNumber $BuildNumber `
        -Channel $Channel `
        -InstallRoot $installRoot `
        -BinDirectory $binDirectory `
        -ReleaseUrl $ReleaseUrl `
        -DownloadBaseUrl $DownloadBaseUrl `
        -NoSetup `
        -NoPathUpdate
    if ($LASTEXITCODE -ne 0) {
        throw "The Ansight installer exited with code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}

Write-Host 'Ansight CLI update complete. Restart any running Ansight host to use the new build.'
