---
name: ansight-create-remote-tool-react-native
description: Use this skill when implementing a custom Ansight remote tool for a React Native app. Create a narrow JavaScript-backed registerTool handler or a native Swift/Kotlin tool, use stable ids, explicit read/write/critical policy, structured results, developer-only registration with __DEV__ or native build gates, runtime ToolGuard access, and verification through the Ansight CLI.
---

# Ansight React Native Remote Tool Skill

Use this skill when a user wants to add an app-specific Ansight remote tool to a React Native app.

Do not use this skill for native-only iOS, native-only Android, .NET, Flutter, server, desktop, or web-only projects.

## What A Remote Tool Is

An Ansight remote tool is an app-specific, agent-facing tool that a developer exposes from inside the running React Native app. It can be registered from JavaScript or native Swift/Kotlin code. Instead of forcing an agent to infer everything from screenshots, logs, component trees, visual trees, or generic reflection, a remote tool lets the agent ask targeted questions about the app's own domain and runtime state. CLI clients discover the tool by its stable id, call it with structured arguments, let the app execute the operation under its active tool guard, and receive a structured JSON-compatible result.

Use a custom remote tool when app-specific state would help an agent understand a bug, explain current behavior, or make a better code fix. For example, a 3D React Native app could expose the active scene id, camera transform, selected entity ids, visible mesh counts, animation state, physics flags, and narrow actions such as selecting an entity, moving the debug camera, or toggling a diagnostic overlay. A map app using Mapbox, Esri, Google Maps, Apple MapKit, or a similar library could expose camera center/zoom/bearing/pitch, loaded style id, sources, layers, tile-load errors, selected feature ids, annotations, current bounds, and safe actions such as toggling a debug layer or flying to a known fixture location.

Prefer a remote tool over broad reflection, ad hoc scripts, or manual simulator/emulator/device poking when the app can expose a precise, reviewable answer to questions such as "why is the map blank?", "why is this model offscreen?", "which domain object is selected?", or "what runtime state differs from the expected fixture?"

Do not treat remote tools as public APIs, production automation endpoints, or general command runners. They are privileged local-development capabilities and must be gated by `__DEV__` or native build configuration, a maximum runtime policy, stable ids, and explicit argument validation.

## Goal

Create the smallest useful React Native custom tool for a local development workflow, register it with the existing Ansight bridge setup, keep it out of distributable builds, guard it at runtime, and verify that the CLI can discover and invoke it.

## Required Constraints

- prefer a JavaScript-backed tool when the operation can safely run in JavaScript
- use native Swift or Kotlin only when the operation needs native-only state or platform APIs
- use a stable id such as `myapp.diagnostics_snapshot`; do not use display text as the id
- prefer one small tool per operation
- choose the narrowest policy: `"read"`, `"write"`, or `"critical"`
- validate flattened string arguments and fail closed on invalid input
- return structured JSON-compatible results
- register custom tools only when the app's development-only condition is true, usually `__DEV__`
- keep native custom tool registration behind Debug or local-development native build gates
- default the active guard to read-only unless the requested workflow needs mutation
- do not expose secrets, arbitrary command dispatch, broad scripting, production endpoints, or destructive app actions

## Workflow

1. Inspect the existing `@ansight/react-native` initialization path and app bootstrap.
2. Identify whether the operation belongs in JavaScript or native code.
3. Identify the development-only condition that controls registration.
4. Implement one narrow tool with stable metadata, explicit policy, argument validation, and a structured result.
5. Register JavaScript-backed tools after Ansight initializes, or register native tools after the native bridge has initialized Ansight.
6. Configure `withReadOnlyToolAccess()`, `withReadWriteToolAccess()`, or `withAllToolAccess()` to match the tool policy.
7. Run JavaScript checks and native builds where practical.
8. Pair the app with the resident CLI host and verify discovery and execution through `ansight app tools` and `ansight app call`.

## JavaScript Tool Pattern

Use JavaScript-backed tools for app state that is already available in React Native:

```ts
import Ansight from "@ansight/react-native";

export async function registerAnsightAppTools(diagnostics: DiagnosticsService) {
  if (!__DEV__) {
    return;
  }

  const registration = Ansight.registerTool(
    {
      id: "myapp.diagnostics_snapshot",
      name: "Diagnostics Snapshot",
      category: "myapp",
      policy: "read",
      description: "Returns a small app-specific diagnostic summary.",
      keywords: "diagnostics health queues",
    },
    async (arguments) => {
      const includeQueues = arguments.includeQueues === "true";
      const snapshot = await diagnostics.createSnapshot();

      return {
        success: true,
        result: {
          connectionState: snapshot.connectionState,
          ...(includeQueues
            ? { pendingQueueCount: snapshot.pendingQueueCount }
            : {}),
        },
      };
    },
  );

  await registration.ready;
}
```

Call `registration.unregister()` or `Ansight.unregisterTool(id)` when a temporary tool should be removed.

## Native Tool Choice

Use native tools when the handler must access Swift or Kotlin-only state, platform APIs, native dependency instances, or native storage that should not be bridged into JavaScript.

For native iOS custom tools, follow:

```text
https://www.ansight.ai/skills/ios/ansight-create-remote-tool-ios.md
```

For native Android custom tools, follow:

```text
https://www.ansight.ai/skills/android/ansight-create-remote-tool-android.md
```

Register native custom tools after the React Native bridge has initialized Ansight. Android runtime initialization rebuilds the native tool registry from bridge options, so tools registered too early can be cleared.

## Registration Timing

Initialize Ansight first, enable the narrowest guard, then register JavaScript-backed tools:

```ts
const builder = Ansight.createOptionsBuilder();

if (__DEV__) {
  builder.withReadOnlyToolAccess();
} else {
  builder.withToolsDisabled();
}

await Ansight.initializeAndActivate(builder.build());
await registerAnsightAppTools(diagnostics);
```

If tools are registered from a feature module, make registration idempotent and use stable ids so repeated app bootstrap does not create duplicate tool entries.

## Policy And Guard Choice

| Policy | Use for | Guard that enables it |
| --- | --- | --- |
| `"read"` | Inspecting app state without mutation. | `withReadOnlyToolAccess()` |
| `"write"` | Creating, updating, replaying, or resetting app state. | `withReadWriteToolAccess()` |
| `"critical"` | Destructive actions, secret access, broad runtime inspection, or arbitrary app-code invocation. | `withAllToolAccess()` |

Raise the maximum to `write` or `critical` only when the workflow genuinely depends on it.

## Verification

Run the app's actual checks, for example:

```bash
npm test
npm run typecheck
npx react-native run-ios
npx react-native run-android
```

Use the package manager and platform commands already used by the app.

After the app is paired and connected:

- `ansight app tools <session-id> --tool-id <tool-id> --detail full --include-unavailable --json` should return the exact custom tool entry and its denial state; invoke it only when `executable` is true
- `ansight app call <session-id> <tool-id> --arguments '{ "includeQueues": true }' --json` should invoke it
- if the tool is missing, check `__DEV__`, initialization order, registration timing, platform build, and guard
- if invocation fails, inspect the returned error, JavaScript logs, native logs, and argument parsing

## Done Criteria

- the tool has a stable id, category, description, policy, and structured result
- custom tool registration runs only in the intended development-only path
- distributable builds omit the tool or keep tools disabled
- JavaScript or native registration happens after the relevant Ansight initialization
- the active guard allows only the required policy
- the CLI can discover and invoke the tool in a paired local development session
- final response names the tool id, policy, JS or native registration path, files changed, and verification performed
