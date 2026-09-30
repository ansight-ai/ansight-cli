# Tasks

A task is a named recipe that runs against one live app session. Think of it as
teaching Ansight a repeatable job once—such as opening a screen, reading app
state, and checking the result—then invoking that exact job whenever you need it.

Read the full [Ansight workspace tasks guide](https://www.ansight.ai/docs/workspace/tasks).

Use a task when the steps should be deterministic and explicitly started. Use a
test when an agent should reason through a user journey, or a trigger when work
should begin automatically after an event.

## Create one

```sh
ansight workspace add task . home.verify \
  --app-id com.example.app \
  --title "Verify signed-in home"
```

The command creates `home.verify.ts`. A nested path such as
`map/validate-areas.ts` has the task ID `map.validate-areas`.

Every module exports a static descriptor and a default function:

```ts
import type { TaskDefinition, TaskInvocation } from "./ansight-task.d.ts";

export const task = {
  "schemaVersion": 1,
  "title": "Verify signed-in home",
  "description": "Checks that the signed-in home is visible.",
  "platforms": ["ios", "android"],
  "deviceKinds": ["virtual", "physical"],
  "frameworks": ["dotnet-maui"],
  "maximumActions": 4
} satisfies TaskDefinition;

export default async function verifyHome({ ansight, expect }: TaskInvocation) {
  const result = await ansight.ui.assert({
    automationId: "signed-in-home",
    exists: true,
    expectedVisible: true
  });
  expect(result.passed, {
    id: "signed-in-home-visible"
  }).toBe(true);
}
```

The descriptor must remain a JSON-compatible object literal because Ansight
reads it without executing the module. A normal return with no named `expect`
assertion is **inconclusive**, not passed.

`platforms`, `deviceKinds`, and `frameworks` are optional task metadata, not
task inputs. A workspace test can reference this task with `taskId`; its CLI
runner validates the selected target and connected framework before execution.
Supported .NET framework IDs are `dotnet-maui`, `dotnet-ios`, and
`dotnet-android`.

`ansight-task.js` exports named permission constants:

```ts
import { Permission, IosPermission, AndroidPermission } from "./ansight-task.js";
await ansight.permissions.grant({ permission: Permission.Camera });
```

Each permission has hover documentation for platform availability, native mappings,
and official platform links. Read the full [Permission API Reference](https://www.ansight.ai/docs/workspace/permissions).
Nested task modules adjust the relative import path to the generated support file.

`ansight-task.d.ts` describes the available APIs and `tsconfig.json` enables
strict editor checks. They are generated support files; put your task logic in
your own `.ts` modules.

Inspect or run tasks with:

```sh
ansight task list --app-id com.example.app --repository .
ansight task run home.verify --app-id com.example.app --repository .
npx tsc -p ansight/tasks/tsconfig.json
```

Task runs require an already connected app session. Add `--headless` to
`task run` or `repo task run` when any permitted tool-requested device starts
should not open simulator/emulator windows. This does not create the initial
session, close existing windows, or restart running emulators.
