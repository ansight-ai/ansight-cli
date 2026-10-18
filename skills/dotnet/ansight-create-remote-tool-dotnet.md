---
name: ansight-create-remote-tool-dotnet
description: Use this skill when implementing a custom Ansight remote tool for a .NET MAUI, .NET for Android, .NET for iOS, or .NET Mac Catalyst app. Create a narrow Ansight.Tools.IJsonTool implementation with stable tool ids, ToolSchema arguments and results, the shared read/write/critical ToolPolicy, local-development MSBuild gating with AnsightRemoteToolsPolicy, registration through AddTool/AddTools or a WithMyAppTools extension, and verification through the Ansight CLI.
---

# Ansight .NET Remote Tool Skill

Use this skill when a user wants to add an app-specific Ansight remote tool that a paired agent can call through the Ansight CLI.

## What A Remote Tool Is

An Ansight remote tool is an app-specific, agent-facing tool that a developer exposes from inside the running app. Instead of forcing an agent to infer everything from screenshots, logs, visual trees, or generic reflection, a remote tool lets the agent ask targeted questions about the app's own domain and runtime state. CLI clients discover the tool by its stable id, call it with structured arguments, let the app execute the operation in-process under its active `ToolGuard`, and receive a structured result.

Use a custom remote tool when app-specific state would help an agent understand a bug, explain current behavior, or make a better code fix. For example, a 3D app could expose the active scene id, camera transform, selected entity ids, visible mesh counts, animation state, physics flags, and narrow actions such as selecting an entity, moving the debug camera, or toggling a diagnostic overlay. A map app using Mapbox, Esri, or a similar library could expose camera center/zoom/bearing/pitch, loaded style id, sources, layers, tile-load errors, selected feature ids, annotations, current bounds, and safe actions such as toggling a debug layer or flying to a known fixture location.

Prefer a remote tool over broad reflection, ad hoc scripts, or manual app poking when the app can expose a precise, reviewable answer to questions such as "why is the map blank?", "why is this model offscreen?", "which domain object is selected?", or "what runtime state differs from the expected fixture?"

Do not treat remote tools as public APIs, production automation endpoints, or general command runners. They are privileged local-development capabilities and must be gated by build configuration, a maximum runtime policy, and stable schemas.

## Goal

Create the smallest useful custom remote tool for a local development workflow, register it with the existing Ansight setup, keep it excluded from protected builds, guard it at runtime, and verify that the CLI can discover and invoke it.

## Required Constraints

- implement `Ansight.Tools.IJsonTool`
- prefer one small tool class per operation
- use a stable id such as `myapp.diagnostics_snapshot`; do not use display text as the id
- declare explicit `ToolSchema` objects for arguments and results
- choose the narrowest `ToolPolicy`: `Read`, `Write`, or `Critical`
- receive structured arguments in `ExecuteAsync(ToolInvocation, CancellationToken)` and validate domain constraints; do not reimplement the inherited dictionary adapter
- return `ToolResult.Failure(...)` with a stable `errorCode` for invalid requests or app-side failures
- register tools only in local development builds
- keep `AnsightRemoteToolsPolicy=AllowedWithWarnings` only when custom tools are intentionally included
- use `AnsightRemoteToolsPolicy=Disallowed` for protected Release, CI, TestFlight, App Store, Play Store, or other distributable builds
- do not expose secrets, account data, production endpoints, arbitrary command dispatch, broad script execution, or destructive app actions

## Workflow

1. Inspect the existing Ansight integration and package shape.
   - MAUI all-in-one apps usually call `builder.UseAnsight<App>(...)`.
   - Non-MAUI all-in-one apps usually call `Options.CreateBuilder().WithAnsightSdk(...)`.
   - Core-only apps build options manually and register tools with `AddTool(...)`, `AddTools(...)`, or suite-specific `With...Tools(...)` methods.
2. Identify the narrow operation the user needs. If the request is broad, split it into multiple read/write/critical tools or implement only the safest first tool.
3. Add a project-local MSBuild flag that includes custom tool code only in local development builds.
4. Create shared metadata for ids and schemas.
5. Implement one `IJsonTool` class for the operation.
6. Register the tool through a project-local extension method such as `WithMyAppTools(...)`.
7. Configure the runtime guard to allow only the needed maximum policy.
8. Build the app and confirm protected builds fail or omit the tool when expected.
9. Run the app, pair it with the resident CLI host, and verify discovery and execution through `ansight app tools` and `ansight app call`.

## MSBuild Pattern

Use an app-specific flag so custom tool code is absent from protected builds:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Debug'">
  <MyAppAnsightRemoteToolsEnabled>true</MyAppAnsightRemoteToolsEnabled>
</PropertyGroup>

<PropertyGroup>
  <MyAppAnsightRemoteToolsEnabled Condition="'$(MyAppAnsightRemoteToolsEnabled)' == ''">false</MyAppAnsightRemoteToolsEnabled>
  <AnsightRemoteToolsPolicy Condition="'$(AnsightRemoteToolsPolicy)' == '' and '$(MyAppAnsightRemoteToolsEnabled)' == 'true'">AllowedWithWarnings</AnsightRemoteToolsPolicy>
  <AnsightRemoteToolsPolicy Condition="'$(AnsightRemoteToolsPolicy)' == ''">Disallowed</AnsightRemoteToolsPolicy>
</PropertyGroup>

<PropertyGroup Condition="'$(MyAppAnsightRemoteToolsEnabled)' == 'true'">
  <DefineConstants>$(DefineConstants);MYAPP_ANSIGHT_TOOLS</DefineConstants>
</PropertyGroup>
```

Wrap custom tool files or classes in the same symbol:

```csharp
#if MYAPP_ANSIGHT_TOOLS
// Custom Ansight tools live here.
#endif
```

Do not manually define `ANSIGHT_REMOTE_TOOLS`. The Ansight SDK defines it when `AnsightRemoteToolsPolicy` resolves to `Allowed` or `AllowedWithWarnings`.

## Recommended Shape

For more than one custom tool, keep the code organized like a small first-party suite:

- `MyAppToolIds`
- `MyAppToolSchemas`
- one `IJsonTool` implementation per operation
- `MyAppAnsightOptionsBuilderExtensions`
- optional wrappers for user confirmation or product-specific access checks

## Shared Metadata

```csharp
#if MYAPP_ANSIGHT_TOOLS
using Ansight.Tools;

internal static class MyAppToolIds
{
    public const string DiagnosticsSnapshot = "myapp.diagnostics_snapshot";
}

internal static class MyAppToolSchemas
{
    public static ToolSchema DiagnosticsSnapshotArguments { get; } = ToolSchema.Object(
        description: "Arguments for reading a small app diagnostic summary.",
        properties: new Dictionary<string, ToolSchema>
        {
            ["includeQueues"] = ToolSchema.Boolean("Include queue counters in the result.")
        });

    public static ToolSchema DiagnosticsSnapshotResult { get; } = ToolSchema.Object(
        description: "App diagnostic summary payload.",
        properties: new Dictionary<string, ToolSchema>
        {
            ["connectionState"] = ToolSchema.String("Current backend connection state."),
            ["pendingQueueCount"] = ToolSchema.Integer("Pending queue count when includeQueues is true.")
        },
        required: new[] { "connectionState" });
}

#endif
```

## Tool Implementation

Prefer `IJsonTool`: arguments retain JSON types and the runtime validates argument and result schemas. Implement only `ExecuteAsync(ToolInvocation, CancellationToken)`; the inherited legacy dictionary adapter is already supplied. Direct calls to the handler bypass protocol validation, so use the tool bridge for tests.

```csharp
#if MYAPP_ANSIGHT_TOOLS
using System.Text.Json.Nodes;
using Ansight.Tools;

internal sealed record DiagnosticsSnapshot(
    string ConnectionState,
    int PendingQueueCount);

internal sealed class DiagnosticsSnapshotTool(
    Func<DiagnosticsSnapshot> snapshotProvider) : IJsonTool
{
    public string Category => "myapp";

    public ToolPolicy Policy => ToolPolicy.Read;

    public string Id => MyAppToolIds.DiagnosticsSnapshot;

    public string Name => "Diagnostics Snapshot";

    public string Description => "Returns a small app-specific diagnostic summary.";

    public string Keywords => "diagnostics health queues";

    public ToolSchema ArgumentsSchema => MyAppToolSchemas.DiagnosticsSnapshotArguments;

    public ToolSchema ResultSchema => MyAppToolSchemas.DiagnosticsSnapshotResult;

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var includeQueues = invocation.Arguments["includeQueues"]?.GetValue<bool>() ?? false;

        var snapshot = snapshotProvider();
        var payload = new JsonObject
        {
            ["connectionState"] = snapshot.ConnectionState
        };

        if (includeQueues)
        {
            payload["pendingQueueCount"] = snapshot.PendingQueueCount;
        }

        return Task.FromResult(ToolResult.Success(payload));
    }
}
#endif
```

For MAUI tools that read or mutate UI state, marshal onto the main thread inside the tool or a shared helper before touching controls, pages, bindings, or handlers.

## Registration

Expose a project-local registration method instead of scattering `new Tool(...)` calls through app startup:

```csharp
#if MYAPP_ANSIGHT_TOOLS
using Ansight;
using Ansight.Tools;

internal static class MyAppAnsightOptionsBuilderExtensions
{
    public static Options.OptionsBuilder WithMyAppTools(
        this Options.OptionsBuilder builder,
        Func<DiagnosticsSnapshot> snapshotProvider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(snapshotProvider);

        return builder.AddTools(new ITool[]
        {
            new DiagnosticsSnapshotTool(snapshotProvider)
        });
    }
}
#endif
```

For a core-only setup:

```csharp
var optionsBuilder = Options.CreateBuilder();

#if MYAPP_ANSIGHT_TOOLS
optionsBuilder = optionsBuilder.WithMyAppTools(diagnostics.CreateSnapshot);
#endif

var options = optionsBuilder
    .WithReadOnlyToolAccess()
    .Build();
```

For the all-in-one package:

```csharp
var options = Options.CreateBuilder()
    .WithAnsightSdk(ansight =>
    {
#if MYAPP_ANSIGHT_TOOLS
        ansight.WithMyAppTools(diagnostics.CreateSnapshot);
#endif
        ansight.WithReadOnlyToolAccess();
    })
    .Build();
```

For MAUI:

```csharp
builder.UseAnsight<App>(ansight =>
{
#if MYAPP_ANSIGHT_TOOLS
    ansight.WithMyAppTools(diagnostics.CreateSnapshot);
#endif
    ansight.WithReadOnlyToolAccess();
});
```

Registered tools stay hidden and unusable until the active guard allows their `ToolPolicy`.

## Policy And Guard Choice

Choose the narrowest policy. A maximum includes every lower policy:

| Policy | Use for | Guard that enables it |
| --- | --- | --- |
| `ToolPolicy.Read` | Inspecting non-secret app state without mutation. | `WithReadOnlyToolAccess()` |
| `ToolPolicy.Write` | Ordinary UI and app-state changes. | `WithReadWriteToolAccess()` |
| `ToolPolicy.Critical` | Destructive actions, secret access, broad runtime inspection, or arbitrary app-code invocation. | `WithAllToolAccess()` |

Use `WithToolsDisabled()` for builds or runtime paths where remote tools must not be discoverable or executable. Use `WithToolGuard(...)` when the app needs a narrower product-specific policy than the built-in presets.

For sensitive internal tools, add a runtime consent or policy wrapper in addition to build-time inclusion and `ToolGuard`. Runtime confirmation is not a substitute for excluding tools from public distribution builds.

## Verification

Build and run the relevant local development target:

```bash
dotnet build path/to/App.csproj -c Debug
```

Also verify a protected configuration when practical:

```bash
dotnet build path/to/App.csproj -c Release
```

After the app is paired and connected, use the CLI:

- `ansight app tools <session-id> --tool-id <tool-id> --detail full --include-unavailable --json` should return the exact custom tool entry and its denial state; invoke it only when `executable` is true.
- `ansight app call <session-id> <tool-id> --arguments '{ "includeQueues": true }' --json` should invoke the tool.
- If the tool is missing, check the app connection, build symbol, `AnsightRemoteToolsPolicy`, registration path, and active guard.
- If invocation fails, inspect the returned `errorCode`, app logs, and argument parsing.

## Done Criteria

- the tool implements `IJsonTool` with stable metadata, schemas, and policy
- custom tool code is included only in the intended local development build
- protected builds omit the tool or fail with `AnsightRemoteToolsPolicy=Disallowed`
- the tool is registered from the existing Ansight startup path
- the active guard allows only the required maximum policy
- the CLI can discover and invoke the tool in a paired local development session
- the final response names the tool id, policy, build flag, files changed, and verification performed

## Optional Factory

For a small operation, `JsonTool.Create` removes the dedicated class while retaining explicit schemas and policy. Use a class when it makes dependencies or logic easier to maintain. This helper requires an SDK release containing it; `IJsonTool` works with earlier releases.

```csharp
var tool = JsonTool.Create(
    new ToolDefinition("myapp.diagnostics_snapshot", "Diagnostics Snapshot",
        "Read current diagnostics.", "myapp", ToolPolicy.Read, "diagnostics",
        MyAppToolSchemas.DiagnosticsSnapshotArguments,
        MyAppToolSchemas.DiagnosticsSnapshotResult),
    (invocation, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = diagnostics.CreateSnapshot();
        return Task.FromResult(ToolResult.Success(new JsonObject
        {
            ["connectionState"] = state.ConnectionState
        }));
    });
optionsBuilder.AddTool(tool);
```

An optional `getAvailabilityAsync` callback supplies dynamic prerequisites. Both forms use the same guard, schema validation, catalog, and registration APIs.

## Execution Context

Execution and availability callbacks have no UI-thread guarantee. Read UI state with the MAUI dispatcher, for example `await MainThread.InvokeOnMainThreadAsync(() => page.Title)`. Do not move an entire file or network operation onto the UI thread. For a 3D engine, use its scene/update dispatcher; the MAUI main thread is not necessarily the engine thread. Availability should be fast and read a thread-safe readiness snapshot.

The cancellation token is cooperative. Native .NET transports currently supply `CancellationToken.None`, so do not assume a CLI timeout stops an in-progress mutation. Validate first, await the operation, and return the actual resulting state. Never hold a scene lock while awaiting a UI or engine dispatcher.

See the [domain-tool sample](https://github.com/ansight-ai/ansight-sdk/tree/main/src/dotnet/samples/Ansight.DomainTools.Sample) for read and write tools, availability, schema validation, and artifact publication. Expose one operation per domain action; avoid a generic command evaluator.
