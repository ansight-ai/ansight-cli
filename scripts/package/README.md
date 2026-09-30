# Build and package the CLI

Build the player before publishing the CLI so its browser UI is embedded:

```sh
npm ci
npm run build:player
RID=linux-x64 scripts/package/publish-cli.sh
RID=linux-x64 scripts/package/package-cli-release.sh
```

Supported RIDs: `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`,
`win-arm64`. These commands build files locally and do not upload them. Release
version/build metadata is read from `ansight.version.props`. Archives include
PolyForm Shield 1.0.0 `LICENSE`, `NOTICE`, and the agent skills.

On Windows:

```powershell
scripts/package/package-cli-release.ps1 -Rid win-x64
```

macOS builds require Xcode. For a local development archive, set
`ALLOW_ADHOC_MACOS_CLI=true` and `CODESIGN_IDENTITY=-`. Public macOS release
archives require Ansight's signing identity and notarization. Linux requires the
normal .NET native runtime dependencies; Android, Appium and device tooling are
needed only for the corresponding device workflows.

Signing, publishing, preview promotion and deployment orchestration remain in
Ansight's private development workspace. No private source is needed to build
the local CLI or harness.
