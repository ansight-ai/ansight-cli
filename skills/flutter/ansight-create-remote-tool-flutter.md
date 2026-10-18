---
name: ansight-create-remote-tool-flutter
description: Use this skill when implementing a custom Ansight remote tool for a Flutter app. Create a narrow Dart registerTool handler with a stable id, explicit read/write/critical policy, structured results, development-only registration, runtime guard access, and CLI verification.
---

# Ansight Flutter Remote Tool Skill

Use this skill to expose app-specific Flutter or Dart state that existing widget, native, artifact, and reflection tools do not answer precisely.

## Workflow

1. Define the narrow question or action and choose a stable namespaced id such as `app.get_sync_state`.
2. Prefer `read`; use `write` for ordinary mutation and `critical` for destructive, secret-bearing, or arbitrary code-invoking operations.
3. Define argument and result schemas where useful.
4. Register after runtime initialization:

```dart
final registration = await Ansight.instance.registerTool(
  const AnsightToolDefinition(
    id: 'app.get_sync_state',
    name: 'Get sync state',
    policy: AnsightToolPolicy.read,
  ),
  (arguments, context) async => const AnsightToolResult.success(
    result: <String, Object?>{'state': 'idle'},
  ),
);
```

5. Keep registration inside `kDebugMode` or the app's explicit developer variant and configure the least-permissive `AnsightToolGuard`.
6. Avoid secrets, unbounded object graphs, raw reflection, and broad arbitrary method invocation.
7. Test handler success, invalid arguments, expected failure, unregistration, denied guard access, and discovery/invocation through `ansight app tools` and `ansight app call`.

Report the tool id, policy, exposed data or mutation, guard, registration location, and verification.

## Authoring and Execution Context

`registerTool(definition, handler)` already supplies the concise form. Define `argumentsSchema` and `resultSchema` on `AnsightToolDefinition`, and choose an explicit policy. The Dart bridge currently supplies string argument values: parse and validate them explicitly; do not assume typed JSON values.

Handlers run on the Dart isolate receiving the native callback. Await mutations before returning; schedule widget changes at a valid point in the Flutter frame lifecycle. Native engine or UI operations must dispatch in the platform implementation. Keep expensive work off the UI isolate. A bridge timeout does not guarantee native cancellation. Dart tools have no pre-invocation availability callback: unregister unavailable tools or return a stable `scene_not_ready` failure.

## Expose a Domain Model

Start with `scene.snapshot` (`read`) returning a bounded JSON projection of scene id, selected entity, and transforms. Add `scene.set_visible` (`write`) taking one entity id and a boolean. Reject unknown ids and a loading scene with stable error codes, dispatch the change to the scene owner, and return the updated visibility. Read current state at invocation time rather than capturing a stale snapshot at registration.

Keep schemas, policy, and dispatch explicit even when using `registerTool`. Verify a successful read, a successful mutation, invalid arguments, an unavailable scene, and a denied write under a read-only guard. For a large model or a durable snapshot, use [artifact publication](https://www.ansight.ai/docs/sdk/flutter/artifacts) from the app instead of embedding it in a tool result.
