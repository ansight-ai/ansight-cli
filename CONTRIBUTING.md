# Contributing

See [README](README.md) for prerequisites and a quick source build. User
documentation and examples are maintained at
[ansight.ai/docs](https://www.ansight.ai/docs).

Read [architecture](docs/architecture.md) and
[code organization](docs/code-organization-conventions.md) before adding features.
Local developer tools must remain account-free. Preserve established wire fields,
public CLR identities and installed storage keys when refactoring.

The host uses the portable `Ansight.Protocol` NuGet package. In a development
workspace, it automatically uses the sibling `ansight-sdk` source project when
present. Set `ANSIGHT_SDK_REPOSITORY` or pass
`-p:AnsightProtocolProject=/path/to/Ansight.Protocol.csproj` to select another
checkout; pass `-p:UseAnsightProtocolSource=false` to use the pinned package.

The CLI embeds player assets from the integrity-pinned `@ansight/player` archive
in `vendor/`. To update it, run `npm run pack:release` in the player repository,
copy the new archive into `vendor/`, run
`npm install --save-dev --save-exact ./vendor/ansight-player-<version>.tgz`,
remove the old archive, and rerun `npm run build:player`. To embed a locally built sibling player, run
`npm --prefix ../ansight-player ci`, `npm --prefix ../ansight-player run build`,
then `dotnet msbuild build/player-assets.proj -p:AnsightPlayerRepository=../ansight-player`
before building the CLI. `ANSIGHT_PLAYER_REPOSITORY` can also select a checkout
containing built `dist/local` assets.

From the repository root:

```sh
npm ci
npm run build:player
dotnet build Ansight.Cli.sln
dotnet test tests/Ansight.Cli.Tests/Ansight.Cli.Tests.csproj --no-build
dotnet test tests/Ansight.Host.Tests/Ansight.Host.Tests.csproj --no-build
node --test scripts/plugins/*.test.mjs scripts/install/*.test.mjs
```

On macOS the host test command also checks native audio lifecycle and simulator
HID recovery. It does not require a running app or simulator. Run the native HID
suite directly with `bash src/Ansight.MacSimulatorHid/Native/Tests/run-tests.sh`.

Use focused tests for the behavior you change. Review namespace and folder
ownership when moving code.

Keep public tests focused on observable local behavior and the optional cloud
boundary: local tools and the resident host work without an account, analytics
respects its consent rules, and a build without private libraries leaves cloud commands
unavailable without affecting local commands. Test each help group once and use
unit tests for history calculations instead of repeating them through long socket
integration scenarios. Cloud account, runner, and upload internals belong in the
private cloud repository's test suite. Keep application-specific regression
fixtures with their application rather than copying its task catalog into this
public repository.

Binary packaging lives in `scripts/package`. Public source builds use public
package feeds and do not require the private publisher or cloud repositories.
First-party contributions use [PolyForm Shield 1.0.0](LICENSE).
