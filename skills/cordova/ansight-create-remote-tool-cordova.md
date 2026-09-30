---
name: ansight-create-remote-tool-cordova
description: Use this skill when implementing a custom Ansight remote tool for a Cordova-family Capacitor app. Create a narrow JavaScript registerTool handler with a stable id, explicit read/write/critical policy, structured results, development-only registration, runtime guard access, and CLI verification.
---

# Ansight Cordova / Capacitor Remote Tool Skill

Use this skill to expose app-specific JavaScript state that existing DOM, native, artifact, and reflection tools do not answer precisely.

## Workflow

1. Define a narrow question or action and choose a stable namespaced id.
2. Prefer `read`; use `write` for ordinary mutation and `critical` for destructive, secret-bearing, or arbitrary code-invoking operations.
3. Register after bridge initialization:

```ts
const registration = Ansight.registerTool(
  {
    id: "app.get_sync_state",
    name: "Get sync state",
    policy: "read",
  },
  async () => ({
    success: true,
    result: { state: "idle" },
  }),
);

await registration.ready;
```

4. Keep registration inside the app's explicit development guard and use the least-permissive tool access.
5. Avoid secrets, full stores, arbitrary JavaScript evaluation, unbounded DOM scraping, and broad method invocation.
6. Test success, invalid inputs, failures, unregistration, denied guard access, and discovery/invocation through `ansight app tools` and `ansight app call`.

Report the tool id, policy, exposed data or mutation, guard, registration location, and verification.
