# Windows installer checks

The PowerShell installer accepts `-Yes` and `-NoSetup` when invoked as a saved script.
For the piped installer, set `ANSIGHT_INSTALL_YES` or `ANSIGHT_INSTALL_NO_SETUP` to
`1`, `true`, or `yes`. Explicit switch arguments override these defaults.

```powershell
$env:ANSIGHT_INSTALL_NO_SETUP = '1'
irm https://www.ansight.ai/install.ps1 | iex
Remove-Item Env:ANSIGHT_INSTALL_NO_SETUP
```

Optional skill installation and host-startup setup failures produce a warning
after the CLI has been installed. Download, checksum, and executable validation
failures still fail the installation.

Run `pwsh -NoProfile -File scripts/install/install.tests.ps1` for the installer
regression checks. The Windows CI job runs these under both Windows PowerShell
and PowerShell 7. The checks execute the installer functions with temporary skill
files; they do not change PATH, install a service, or download a CLI release.
