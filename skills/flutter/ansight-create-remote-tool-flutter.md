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
