# Host and CLI code organization

These conventions apply to the ten C# projects in `Ansight.Cli.sln`.

Production .NET projects and native sources live under `src/`. Test projects live
under `tests/`. Keep build definitions in `build/`, platform and integration
tooling in `scripts/`, and contributor documentation in `docs/`. The root solution,
shared MSBuild properties, SDK pin and release version file remain at the root.

## Feature ownership

Place implementations, interfaces, and data contracts with the feature that owns
their behavior. Use subfolders when a feature has distinct responsibilities, such
as `Sessions/Artifacts` or `Simulator/Android/Input`.
Avoid project-wide `Models`, `Contracts`, or `Public` collections. Visibility alone
does not identify an owner.

Shared command behavior belongs outside an individual command: graph planning is
in `src/Ansight.Cli/AppGraphs`, host transport in `HostConnection`, and settings storage
in `Configuration`. Command dispatch and command-specific output stay in `Commands`.
Shared navigation metadata belongs in `UiAutomation/Navigation`; model prompt
guidance belongs in `SimulatorAgent/Prompts`.

Keep independently consumed types in files named for those types. Small private
nested helpers can remain with their owner. Cross-feature results have an explicit
common home, such as `Runtime/Common`; cohesive state interfaces stay together in
`Runtime/State`.

Keep optional hosted implementations in the private cloud repository. Public
consumers depend on feature contracts and load those implementations only when
a cloud operation is requested.

## Names and namespaces

New namespaces match the project root plus relative folder. Internal names should
express their role without repeating the entire namespace: `TextInputEncoder` in
`Simulator.Android.Input`, `InputDispatcher` in `Server.WebRtc`, and `ControlClient`
in `Cli.HostConnection`. Retain domain qualifiers when they distinguish types used
together, such as public ADB and SimCtl contracts. Avoid ambiguous blanket renames
to `Service`, `Request`, or `Result`.

Private and internal fields use lowerCamelCase without a leading underscore.
Use a named record, struct, or class for shared domain values instead of a tuple
whose meaning depends on positional ordering.

Existing public CLR type names and namespaces are compatibility boundaries. A
public type may move physically into its owning feature while retaining its
published namespace. New public types use their feature namespace. Infrastructure
types use `Ansight.Infrastructure.*` with feature folders matching the suffix.
This namespace change is an intentional API break; do not add aliases for the
retired desktop product.

A partial class has one owning file in its namespace folder. Feature partials can
live beneath that folder while sharing the owning namespace. For example,
`Explorer/Devices/ExplorerServer.Devices.cs` shares `Ansight.Host.Explorer` with
`Explorer/ExplorerServer.cs`. Standalone types in that folder use
`Ansight.Host.Explorer.Devices` unless compatibility requires an existing namespace.

## Tests and validation

Group CLI tests by feature. Host and supporting-library coverage belongs in
`Ansight.Host.Tests`, with feature namespaces matching its `Unit` or `Integration`
folders. Shared test helpers belong in `TestSupport`.

From the repository root, run:

```sh
dotnet build Ansight.Cli.sln
dotnet test Ansight.Cli.sln --no-build
```

Review feature ownership, namespace alignment, naming clarity, and any new
compatibility exception when moving code.
