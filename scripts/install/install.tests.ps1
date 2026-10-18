$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tokens = $null
$parseErrors = $null
$installer = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'install.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }

# Exercise the real installer functions without downloading or modifying a workstation.
foreach ($name in @('Read-YesNo', 'Install-AnsightSkill', 'Invoke-AnsightOptionalSetup')) {
    $definition = $installer.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$originalYes = $env:ANSIGHT_INSTALL_YES
$originalNoSetup = $env:ANSIGHT_INSTALL_NO_SETUP
$directory = Join-Path ([IO.Path]::GetTempPath()) ('ansight-installer-test-' + [guid]::NewGuid().ToString('N'))
try {
    $readFlags = [scriptblock]::Create($installer.ParamBlock.Extent.Text + @'

[pscustomobject]@{ Yes = [bool]$Yes; NoSetup = [bool]$NoSetup }
'@)
    foreach ($value in @('1', 'true', 'YES')) {
        $env:ANSIGHT_INSTALL_YES = $value
        $env:ANSIGHT_INSTALL_NO_SETUP = $value
        $flags = & $readFlags
        Assert-True ($flags.Yes -and $flags.NoSetup) "Environment flags did not accept $value."
        $flags = & $readFlags -Yes:$false -NoSetup:$false
        Assert-True (-not $flags.Yes -and -not $flags.NoSetup) 'Explicit flags must override environment defaults.'
    }
    foreach ($value in @('', '0', 'false', 'no')) {
        $env:ANSIGHT_INSTALL_YES = $value
        $env:ANSIGHT_INSTALL_NO_SETUP = $value
        $flags = & $readFlags
        Assert-True (-not $flags.Yes -and -not $flags.NoSetup) "Environment flags incorrectly accepted $value."
    }

    $script:SkillsRoot = Join-Path $directory 'source'
    $skill = Join-Path $script:SkillsRoot 'ansight-cli-setup'
    New-Item -ItemType Directory -Path $skill -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $skill 'SKILL.md') -Value 'Test skill'
    $destination = Join-Path $directory 'installed'
    $script:SkillInstallNoticeShown = $false
    $archiveUrl = 'https://example.invalid/test.zip'
    $Yes = $false
    $script:observedPrompt = ''
    function Read-Host([string]$Prompt) { $script:observedPrompt = $Prompt; return 'y' }
    Install-AnsightSkill -Label 'Regression test client' -DestinationRoot $destination
    Assert-True ($script:observedPrompt -eq 'Install the Ansight agent skills for Regression test client? [y/N]') 'Skill prompt changed or failed under StrictMode.'
    Assert-True (Test-Path (Join-Path $destination 'ansight-cli-setup/SKILL.md')) 'Accepted skill installation did not copy the skill.'

    $Yes = $true
    function Read-Host { throw 'Unattended setup must not prompt.' }
    Assert-True (Read-YesNo -Prompt 'Unattended test') 'Yes flag did not suppress prompting.'

    $warnings = @()
    Invoke-AnsightOptionalSetup { throw 'Synthetic setup failure' } -WarningVariable warnings
    Assert-True ($warnings.Count -eq 1) 'Optional setup failure must be reported as a warning.'
    Assert-True ($warnings[0].ToString().Contains('Ansight is installed')) 'Warning must distinguish installed CLI from optional setup failure.'
    $script:setupRan = $false
    Invoke-AnsightOptionalSetup { $script:setupRan = $true }
    Assert-True $script:setupRan 'Successful setup must execute.'
    Write-Host 'Windows installer regression checks passed.'
}
finally {
    $env:ANSIGHT_INSTALL_YES = $originalYes
    $env:ANSIGHT_INSTALL_NO_SETUP = $originalNoSetup
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
}
