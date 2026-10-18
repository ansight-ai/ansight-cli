---
name: ansight-create-remote-tool-ios
description: Use this skill when implementing a custom Ansight remote tool for a native iOS SwiftUI or UIKit app. Create a narrow AnsightJSONTool with a stable descriptor id, explicit read/write/critical policy, structured result payloads, Debug or local-development build gating, runtime tool guard access, registration through AnsightRuntime.shared.registerTool, and verification through the Ansight CLI.
---

# Ansight iOS Remote Tool Skill

Use this skill when a user wants to add an app-specific Ansight remote tool to a native iOS app.

Do not use this skill for React Native, .NET iOS, Flutter, server, desktop, or generic Swift Package projects.

## What A Remote Tool Is

An Ansight remote tool is an app-specific, agent-facing tool that a developer exposes from inside the running iOS app. Instead of forcing an agent to infer everything from screenshots, logs, visual trees, or generic reflection, a remote tool lets the agent ask targeted questions about the app's own domain and runtime state. CLI clients discover the tool by its stable id, call it with structured arguments, let the app execute the operation in-process under its active tool guard, and receive a structured result payload.

Use a custom remote tool when app-specific state would help an agent understand a bug, explain current behavior, or make a better code fix. For example, a 3D iOS app could expose the active scene id, camera transform, selected entity ids, visible mesh counts, animation state, physics flags, and narrow actions such as selecting an entity, moving the debug camera, or toggling a diagnostic overlay. A map app using Mapbox, Esri, Apple MapKit, or a similar library could expose camera center/zoom/bearing/pitch, loaded style id, sources, layers, tile-load errors, selected feature ids, annotations, current bounds, and safe actions such as toggling a debug layer or flying to a known fixture location.

Prefer a remote tool over broad reflection, ad hoc scripts, or manual simulator/device poking when the app can expose a precise, reviewable answer to questions such as "why is the map blank?", "why is this model offscreen?", "which domain object is selected?", or "what runtime state differs from the expected fixture?"

Do not treat remote tools as public APIs, production automation endpoints, or general command runners. They are privileged local-development capabilities and must be gated by Debug or local-development build settings, a maximum runtime policy, stable ids, and explicit argument validation.

## Goal

Create the smallest useful iOS custom tool for a local development workflow, register it with the existing Ansight runtime setup, keep it out of distributable builds, guard it at runtime, and verify that the CLI can discover and invoke it.

## Required Constraints

- implement an executable `AnsightJSONTool` for app-specific operations
- use a stable id such as `myapp.diagnostics_snapshot`; do not use display text as the id
- prefer one small tool per operation
- choose the narrowest policy: read, write, or critical
- declare argument/result schemas and fail closed on invalid input
- return structured result payloads
- compile or register custom tools only in Debug or an explicit local-development build path
- set `ANSIGHT_ALLOW_REMOTE_TOOLS=true` only for builds that intentionally include remote tools
- default the active guard to read-only unless the requested workflow needs mutation
- do not expose secrets, arbitrary command dispatch, broad scripting, production endpoints, or destructive app actions

## Workflow

1. Inspect the existing Ansight install path, package manager, app target, and SwiftUI or UIKit startup.
2. Identify the narrow operation the user needs. Split broad requests into separate read/write/critical tools.
3. Choose a local-development build gate:
   - `#if DEBUG` for simple Debug-only tools
   - a project-specific Swift active compilation condition for internal developer builds
   - target membership limited to a developer-only target when appropriate
4. Implement `AnsightJSONTool` or use `AnsightFunctionJSONTool` with stable descriptor metadata.
5. Register the tool after Ansight initializes or during the same startup path.
6. Configure `.withReadOnlyToolAccess()`, `.withReadWriteToolAccess()`, or `.withAllToolAccess()` to match the tool policy.
7. Build a Debug target and, when practical, a Release target that omits the tool or disables tools.
8. Pair the app with the resident CLI host and verify discovery and execution through `ansight app tools` and `ansight app call`.

## Implementation Pattern

Use a concrete executable tool for app-owned operations:

```swift
#if DEBUG
import Ansight

struct DiagnosticsSnapshotTool: AnsightJSONTool {
    let diagnostics: DiagnosticsService

    var descriptor: AnsightToolDescriptor {
        AnsightToolDescriptor(
            id: "myapp.diagnostics_snapshot",
            name: "Diagnostics Snapshot",
            description: "Returns a small app-specific diagnostic summary.",
            category: "myapp",
            policy: .read,
            keywords: "diagnostics health queues",
            argumentsSchema: .init(json: .object([
                "type": .string("object"),
                "properties": .object(["includeQueues": .object(["type": .string("boolean")])]),
                "additionalProperties": .bool(false)
            ])),
            resultSchema: .init(json: .object([
                "type": .string("object"),
                "properties": .object([
                    "connectionState": .object(["type": .string("string")]),
                    "pendingQueueCount": .object(["type": .string("number")])
                ]),
                "required": .array([.string("connectionState")]),
                "additionalProperties": .bool(false)
            ]))
        )
    }

    func execute(arguments: [String: JSONValue]) throws -> AnsightToolExecutionResult {
        let includeQueues = arguments["includeQueues"] == .bool(true)
        let snapshot = diagnostics.createSnapshot()

        var result: [String: JSONValue] = [
            "connectionState": .string(snapshot.connectionState)
        ]

        if includeQueues {
            result["pendingQueueCount"] = .number(Double(snapshot.pendingQueueCount))
        }

        return .success(.object(result))
    }
}
#endif
```

For UI state, marshal onto the main actor before touching SwiftUI, UIKit, views, scenes, delegates, or navigation state.

## Registration

Register from the existing Ansight startup path:

```swift
try AnsightRuntime.shared.initializeAndActivateAnsightSdk { options in
#if DEBUG
    options.withReadOnlyToolAccess()
#else
    options.withToolsDisabled()
#endif
}

#if DEBUG
try AnsightRuntime.shared.registerTool(
    DiagnosticsSnapshotTool(diagnostics: diagnostics),
    replaceExisting: true
)
#endif
```

Descriptor-only registration is useful for catalog metadata, but app-specific executable tools should provide a concrete `AnsightTool`.

Registered tools stay hidden and unusable until the active guard allows their policy.

## Build Gating

Use Debug or a named local-development condition for custom tools. Protected Release, TestFlight, App Store, CI release, and other distributable builds should omit custom tool code or initialize Ansight with tools disabled.

If the project uses a build setting for remote tools, keep it limited to local development:

```text
ANSIGHT_ALLOW_REMOTE_TOOLS=true
```

Enrollment does not use a build setting or bundled config. A Simulator registers
automatically; for a physical iPhone, run `ansight pairing issue --qr` and scan
the generic terminal QR once. The SDK supplies its real App ID.

Do not set `ANSIGHT_ALLOW_REMOTE_TOOLS=true` for distributable builds.

## Policy And Guard Choice

| Policy | Use for | Guard that enables it |
| --- | --- | --- |
| `AnsightToolPolicy.read` | Inspecting non-secret app state without mutation. | `withReadOnlyToolAccess()` |
| `AnsightToolPolicy.write` | Ordinary UI and app-state changes. | `withReadWriteToolAccess()` |
| `AnsightToolPolicy.critical` | Destructive actions, secret access, broad runtime inspection, or arbitrary app-code invocation. | `withAllToolAccess()` |

Use a custom guard only when the app needs narrower product-specific policy than the built-in presets.

## Verification

Run the relevant local development build:

```bash
xcodebuild -scheme MyApp -configuration Debug -destination 'platform=iOS Simulator,name=iPhone 16' build
```

When practical, also run a release-style build without developer enrollment or remote-tool flags:

```bash
xcodebuild -scheme MyApp -configuration Release -destination 'generic/platform=iOS' build
```

After the app is paired and connected:

- `ansight app tools <session-id> --tool-id <tool-id> --detail full --include-unavailable --json` should return the exact custom tool entry and its denial state; invoke it only when `executable` is true
- `ansight app call <session-id> <tool-id> --arguments '{ "includeQueues": true }' --json` should invoke it
- if the tool is missing, check the active session, compilation condition, target membership, registration path, and guard
- if invocation fails, inspect the returned error, app logs, and argument parsing

## Done Criteria

- the tool has a stable descriptor id, category, description, policy, and structured result
- custom tool code is included only in the intended local development build
- distributable builds omit the tool or keep tools disabled
- the tool is registered from the existing Ansight startup path
- the active guard allows only the required maximum policy
- the CLI can discover and invoke the tool in a paired local development session
- final response names the tool id, policy, build gate, files changed, and verification performed

## Authoring and Execution Context

Use `AnsightFunctionJSONTool(descriptor:availability:handler:)` for a small operation, or implement `AnsightJSONTool` for a larger one. Both receive `[String: JSONValue]` with runtime schema validation, and the legacy string adapter is supplied automatically. The closure helper requires an SDK release containing it.

Execution and availability are synchronous on the transport context, without a main-actor guarantee. Use an app-owned dispatcher for UI or engine work; check whether you are already on the target queue before a synchronous dispatch. Never synchronously dispatch back to a queue that is waiting for your tool. The engine update thread can differ from the UI thread. Keep availability checks fast and thread-safe; do not assume a caller timeout cancels native work.

## Expose a Domain Model

Start with `scene.snapshot` (`read`) returning a bounded JSON projection of scene id, selected entity, and transforms. Add `scene.set_visible` (`write`) taking one entity id and a boolean. Reject unknown ids and a loading scene with stable error codes, dispatch the change to the scene owner, and return the updated visibility. Read current state at invocation time rather than capturing a stale snapshot at registration.

Keep schemas, policy, and dispatch explicit even when using `AnsightFunctionJSONTool`. Verify a successful read, a successful mutation, invalid arguments, an unavailable scene, and a denied write under a read-only guard. For a large model or a durable snapshot, use [artifact publication](https://www.ansight.ai/docs/sdk/ios/artifacts) from the app instead of embedding it in a tool result.
