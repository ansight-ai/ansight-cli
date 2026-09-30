---
name: ansight-create-remote-tool-android
description: Use this skill when implementing a custom Ansight remote tool for a native Android Kotlin or Java app. Create a narrow androidSimpleTool or AndroidTool with a stable id, explicit read/write/critical ToolPolicy, structured JSON results, local-development build-variant gating, runtime ToolGuard access, registration through AnsightOptions.addTool/addTools or AnsightRuntime.registerTool, and verification through the Ansight CLI.
---

# Ansight Android Remote Tool Skill

Use this skill when a user wants to add an app-specific Ansight remote tool to a native Android app.

Do not use this skill for React Native, .NET Android, Flutter, server, desktop, or generic JVM projects.

## What A Remote Tool Is

An Ansight remote tool is an app-specific, agent-facing tool that a developer exposes from inside the running Android app. Instead of forcing an agent to infer everything from screenshots, logs, visual trees, or generic reflection, a remote tool lets the agent ask targeted questions about the app's own domain and runtime state. CLI clients discover the tool by its stable id, call it with structured arguments, let the app execute the operation in-process under its active tool guard, and receive a structured JSON result.

Use a custom remote tool when app-specific state would help an agent understand a bug, explain current behavior, or make a better code fix. For example, a 3D Android app could expose the active scene id, camera transform, selected entity ids, visible mesh counts, animation state, physics flags, and narrow actions such as selecting an entity, moving the debug camera, or toggling a diagnostic overlay. A map app using Mapbox, Esri, Google Maps, or a similar library could expose camera center/zoom/bearing/tilt, loaded style id, sources, layers, tile-load errors, selected feature ids, annotations, current bounds, and safe actions such as toggling a debug layer or flying to a known fixture location.

Prefer a remote tool over broad reflection, ad hoc scripts, or manual emulator/device poking when the app can expose a precise, reviewable answer to questions such as "why is the map blank?", "why is this model offscreen?", "which domain object is selected?", or "what runtime state differs from the expected fixture?"

Do not treat remote tools as public APIs, production automation endpoints, or general command runners. They are privileged local-development capabilities and must be gated by build variant, a maximum runtime policy, stable ids, and explicit argument validation.

## Goal

Create the smallest useful Android custom tool for a local development workflow, register it with the existing Ansight runtime setup, keep it out of distributable builds, guard it at runtime, and verify that the CLI can discover and invoke it.

## Required Constraints

- prefer Kotlin for new Android tool code when the app uses Kotlin
- use a stable id such as `myapp.diagnostics_snapshot`; do not use display text as the id
- prefer one small tool per operation
- choose the narrowest `ToolPolicy`: `Read`, `Write`, or `Critical`
- validate flattened string arguments and fail closed on invalid input
- return structured JSON results
- place custom tool code in `src/debug/...` when Ansight is a `debugImplementation`
- keep custom tool code out of Play Store, CI release, and other distributable variants
- default the active guard to read-only unless the requested workflow needs mutation
- do not expose secrets, arbitrary command dispatch, broad scripting, production endpoints, or destructive app actions

## Workflow

1. Inspect the existing Ansight install path, Gradle dependency scope, app module, and `Application` startup.
2. Identify the narrow operation the user needs. Split broad requests into separate read/write/critical tools.
3. Choose where the tool can compile safely:
   - `src/debug/kotlin` or `src/debug/java` when the Ansight dependency is `debugImplementation`
   - a local-development flavor/source set when the app uses internal variants
   - main source only when the dependency is intentionally present there and guarded
4. Implement the tool with `androidSimpleTool(...)` or a concrete Android tool type that matches the existing codebase.
5. Register the tool before building `AnsightOptions`, or register it at runtime only after Ansight has initialized.
6. Configure `withReadOnlyToolAccess()`, `withReadWriteToolAccess()`, or `withAllToolAccess()` to match the tool policy.
7. Build a debug target and, when practical, a release target that omits the tool.
8. Pair the app with the resident CLI host and verify discovery and execution through `ansight app tools` and `ansight app call`.

## Implementation Pattern

Use `androidSimpleTool(...)` for small app-owned operations:

```kotlin
import ai.ansight.runtime.AndroidToolResult
import ai.ansight.runtime.ToolPolicy
import ai.ansight.runtime.androidSimpleTool
import org.json.JSONObject

val diagnosticsSnapshotTool = androidSimpleTool(
    id = "myapp.diagnostics_snapshot",
    name = "Diagnostics Snapshot",
    description = "Returns a small app-specific diagnostic summary.",
    category = "myapp",
    policy = ToolPolicy.Read,
    keywords = "diagnostics health queues",
) { arguments, _ ->
    val includeQueues = arguments["includeQueues"]?.toBooleanStrictOrNull() ?: false
    val snapshot = diagnostics.createSnapshot()

    AndroidToolResult.success(
        JSONObject()
            .put("connectionState", snapshot.connectionState)
            .apply {
                if (includeQueues) {
                    put("pendingQueueCount", snapshot.pendingQueueCount)
                }
            }
    )
}
```

For arguments that are required or constrained, return a failure result instead of silently guessing. Keep error messages stable enough for an agent to act on.

## Registration

Register tools from the same startup path that builds Ansight options:

```kotlin
val options = Ansight.developerOptions(
    clientName = "Android App",
)
    .apply {
        if (BuildConfig.DEBUG) {
            addTool(diagnosticsSnapshotTool)
            withReadOnlyToolAccess()
        } else {
            withToolsDisabled()
        }
    }
    .build()
```

Use runtime registration only when the tool depends on state that is unavailable during options construction:

```kotlin
if (BuildConfig.DEBUG) {
    AnsightRuntime.registerTool(diagnosticsSnapshotTool, replaceExisting = true)
}
```

Registered tools stay hidden and unusable until the active guard allows their `ToolPolicy`.

## Build Variant Gating

When the app uses:

```kotlin
debugImplementation("ai.ansight:ansight-android:1.4.0-preview.1")
```

put custom tool code under the debug source set:

```text
app/src/debug/kotlin/com/example/app/ansight/DiagnosticsSnapshotTool.kt
```

If release code needs to call a shared registration hook, create a no-op release implementation with the same app-owned API. Do not make release depend on Ansight just to satisfy debug-only tool code.

## Policy And Guard Choice

| Policy | Use for | Guard that enables it |
| --- | --- | --- |
| `ToolPolicy.Read` | Inspecting non-secret app state without mutation. | `withReadOnlyToolAccess()` |
| `ToolPolicy.Write` | Ordinary UI and app-state changes. | `withReadWriteToolAccess()` |
| `ToolPolicy.Critical` | Destructive actions, secret access, broad runtime inspection, or arbitrary app-code invocation. | `withAllToolAccess()` |

Use a custom guard only when the app needs narrower product-specific policy than the built-in presets.

## Verification

Run the relevant local development build:

```bash
./gradlew :app:assembleDebug
```

When practical, also run:

```bash
./gradlew :app:assembleRelease
```

After the app is paired and connected:

- `ansight app tools <session-id> --tool-id <tool-id> --detail full --include-unavailable --json` should return the exact custom tool entry and its denial state; invoke it only when `executable` is true
- `ansight app call <session-id> <tool-id> --arguments '{ "includeQueues": true }' --json` should invoke it
- if the tool is missing, check the active session, source set, dependency scope, registration path, and guard
- if invocation fails, inspect the returned error, app logs, and argument parsing

## Done Criteria

- the tool has a stable id, category, description, policy, and structured JSON result
- custom tool code is included only in the intended local development variant
- distributable variants omit the tool or keep tools disabled
- the tool is registered from the existing Ansight startup path
- the active guard allows only the required maximum policy
- the CLI can discover and invoke the tool in a paired local development session
- final response names the tool id, policy, source set or build variant, files changed, and verification performed
