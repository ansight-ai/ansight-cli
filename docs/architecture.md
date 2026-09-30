# CLI and host architecture

`Ansight.Cli.sln` contains the command adapter, local runtime, device bridges and
.NET test suites. `Ansight.Tray.Windows` is built separately for Windows packages.

- `Ansight.Cli` parses commands and talks to the resident host.
- `Ansight.Host` owns app discovery, pairing, capture, sessions, automation,
  local replay and direct model execution using a developer-provided credential.
- `Ansight.Infrastructure` owns local storage, settings and optional service
  contracts. `Ansight.Protocol` supplies portable messages from the public SDK.
- ADB, SimCtl, HID and RTC projects own their platform adapters and native code.
- Browser UI assets come from the versioned public `@ansight/player` package.

Account authentication, cloud uploads/sharing, hosted runners and brokered AI
live in private assemblies. The private `Ansight.Cloud.App` executable references
them at build time; the public CLI has no private project references and builds
without the cloud checkout. Service factories defer account initialization until
a cloud feature requests one. Local startup and capability discovery remain
account-free.

SDK runtime source and the shared player are independent repositories. The iOS
companion app is parked in its own private repository. Release/deployment tooling
belongs to the private publisher; release history belongs to GitHub Releases.

Workspace initialization templates and schema/type contracts are embedded in the
host and ship with the CLI. Optional teaching samples live on the website.

See [code organization](code-organization-conventions.md) for feature ownership,
namespace compatibility and validation conventions.
