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

## Authoring and Execution Context

`registerTool(definition, handler)` already supplies the concise form for Capacitor. Define `argumentsSchema` and `resultSchema` and choose an explicit policy. The bridge currently supplies flattened string arguments, so validate and parse booleans, numbers, and JSON fields explicitly.

Handlers execute in the WebView JavaScript runtime. Await state changes before returning and keep heavy work off the UI event loop. Native plugins must dispatch UI and engine access to their owning queues. A bridge timeout does not guarantee cancellation. JavaScript tools have no pre-invocation availability callback: unregister unavailable tools or return a stable `scene_not_ready` failure.

## Expose a Domain Model

Start with `scene.snapshot` (`read`) returning a bounded JSON projection of scene id, selected entity, and transforms. Add `scene.set_visible` (`write`) taking one entity id and a boolean. Reject unknown ids and a loading scene with stable error codes, dispatch the change to the scene owner, and return the updated visibility. Read current state at invocation time rather than capturing a stale snapshot at registration.

Keep schemas, policy, and dispatch explicit even when using `registerTool`. Verify a successful read, a successful mutation, invalid arguments, an unavailable scene, and a denied write under a read-only guard. For a large model or a durable snapshot, use [artifact publication](https://www.ansight.ai/docs/sdk/cordova/artifacts) from the app instead of embedding it in a tool result.
