---
name: ansight-ui-testing
description: Initialize, author, extract, debug, validate, run, or maintain source-controlled Ansight workspace automation. Use for tests, tasks, triggers, trends, sanitizers, schemas, or workspace registration; do not use for routine live interaction, session investigation, or readiness scoring unless needed to build or verify a workspace asset.
---

# Ansight Workspace Automation

Build and maintain the repository-owned `ansight/` playbook for one app. Prefer the Ansight CLI for workspace initialization, scaffolding, discovery, execution, and validation. Edit the generated source files when their behavior or policy must be implemented.

## Understand The Workspace Components

| Component | What it accomplishes | Why use it |
| --- | --- | --- |
| Test (`ansight/tests/**/*.yaml`, with `.yml` and legacy `.json` accepted) | Gives a bounded agent a natural-language user journey and observable final-state validation. | Use it when the path requires observation or decisions and success can be proved through Ansight evidence. |
| Task (`ansight/tasks/**/*.ts`) | Encodes an explicitly invoked, deterministic sequence of known tool calls with named assertions. | Use it to make a stable app-specific workflow faster, cheaper, and repeatable without asking an agent to rediscover every step. |
| Trigger (`ansight/triggers/**/*.ts`) | Reacts automatically to one matching app or session event and may request one bounded app action. | Use it to enrich future session evidence at a meaningful moment, such as capturing app-owned diagnostics after an error. Do not use it as a scheduler or general background worker. |
| Trends (`ansight/trends/**/*.json`) | Defines a stable span from SDK/host events or captured logs, calculates deterministic telemetry metrics, evaluates fixed budgets, and optionally compares each metric with comparable historical runs. | Use it for repeatable FPS, memory, duration, evidence, or other numeric trend rules and regression monitoring rather than subjective agent judgment. |
| Sanitizer (`ansight/sanitizers/**/*.ts`) | Redacts, transforms, or removes sensitive content while Ansight creates a separate session archive. | Use it before exporting or sharing captured data; it does not modify the original session. |
| Schemas and support files (`ansight/schema`, declarations, and `tsconfig.json`) | Provide versioned contracts, editor completion, and local or CI validation. | Use them to author against the installed host contract and catch invalid definitions before discovery or execution. They are not runtime workflows. |

A test is agentic; a task is deterministic; a trigger is event-driven; Trends turns event instrumentation into an observation span, metric budgets, and optional regression monitoring; and a sanitizer protects an exported copy.

## Prerequisites And Scope

Authoring and validating a workspace require the Ansight CLI, but they do not require a connected app. If the executable or local host is not ready, report the missing prerequisite. Use setup only within the requested scope: `https://www.ansight.ai/skills/ansight-cli-setup.md`.

Running automation requires the matching App ID and a session or launch target with the required capabilities. SDK and external device sessions both support visible UI workflows and host evidence. Preserve the user's capture choice; when unresolved, use `https://www.ansight.ai/skills/agents/ansight-operate-live-app/SKILL.md` for mode selection and external setup. In-process app tools and App Graphs need an SDK provider. Install SDK features only when in scope: `https://www.ansight.ai/skills/ansight-install.md`. Do not require the SDK merely to edit definitions or inspect retained evidence.

External tasks and triggers use descriptor `schemaVersion: 2`, declare essential `requires.capabilities` or `requires.appTools`, and inspect `ansight.capabilities.snapshot()` for optional task features. Version 1 retains its SDK requirement. Keep test and Trends schema versions independent of task/trigger versions. A missing capability refuses execution; never skip it and report success. External user touches and instrumented network evidence are unavailable; native logs, metrics, and files depend on the selected target.

Workspace authoring does not authorize execution. Run a test or task, connect a trigger, rebuild Trends history, export a sanitized archive, or synchronize cloud history only when the user requested that state-changing operation or verification.

For workspace automation requests, prefer existing capabilities in the selected SDK or external session. Verify their execution, transfer, and persistence behavior before concluding that app changes are required. An unverified capability is an uncertainty, not evidence that a new tool is needed.

Preserve the requested outcome: copying a file does not automatically require a transactional snapshot or custom artifact provider. If existing capabilities demonstrably cannot satisfy the request, explain the specific gap and propose app integration work separately, unless that work is already authorized.

## Inspect And Initialize With The CLI

Resolve the repository root and exact App ID, then inspect the existing `ansight/` tree before adding anything:

```sh
ansight app list --json
ansight app get <app-id> --json
```

Initialize missing workspace directories and canonical support files with:

```sh
ansight workspace init <repository-root> --app-id <app-id> --json
```

Registration links the App ID to the trusted codebase and enables automatic Trends evaluation when matching sessions finalize. Use `--no-register` only when the user wants scaffolding without that link. Initialization is idempotent and preserves customized files; do not use `--force` unless replacing support files is explicitly intended.

When a workspace already exists but its app registration must be added or repaired, use:

```sh
ansight app register <app-id> --codebase <repository-root> --json
```

## Scaffold Before Editing

Use the CLI generators when they exist so collision checks, canonical declarations, and current defaults come from the installed host:

```sh
ansight workspace add test <repository-root> <id> --app-id <app-id> --json
ansight workspace add task <repository-root> <id> --app-id <app-id> --json
ansight workspace add trigger <repository-root> <id> --app-id <app-id> --json
ansight workspace add sanitizer <repository-root> <id> --json
```

Then edit the generated definition. Existing files fail safely unless `--force` is supplied; do not replace one silently. There is no `workspace add trends` command, so create strict JSON files from the current [Trends contract](https://www.ansight.ai/docs/workspace/trends).

Derive stable IDs from paths by removing the extension and replacing directory separators with dots. Preserve local naming conventions. Use YAML block text for test prompts, strict portable JSON for Trends, and the canonical declarations produced by the installed host. Tests, Trends, and sanitizers use their own version 1 contracts; tasks and triggers use version 2 when capability-aware external execution is intended. Do not invent runtime APIs or schema fields.

## Tests: Agentic Journeys

Read the current [Workspace Tests](https://www.ansight.ai/docs/workspace/tests) contract before authoring. Put actions and observable checks together in `prompt`, checking transient state at the relevant point in the journey. `validation` is optional; retain it when a separate checklist is useful. Use `@task/ID` for an exact workspace task and `@selector/ID` for an exact observed automation ID; percent-encode special characters in IDs. The YAML editor completes both references from workspace tasks and recorded automation IDs. Unknown or disabled task references fail before execution; an unrecorded selector needs evidence review. Require the exact `appId`. Put only secret aliases in `requiredSecrets`, never secret values. Trends evaluation is session-driven and independent of workspace test outcomes.

List and validate before execution:

```sh
ansight test list <repository-root> --json
ansight test validate <repository-root> --json
```

To extract a workspace YAML test from an existing replay interval, use the
session player's **Extract test or task → Ansight test** option or the CLI:

```sh
ansight task extract <session-id> --start <seconds-or-timestamp> --end <seconds-or-timestamp> \
  --format test --workspace <repository-root> --title <title> \
  --assertion "<observable final state>" --json
```

The extractor writes `ansight/tests/<suggested-name>.yaml` with a recorded
journey prompt and validation assertions. Repeat `--assertion` for multiple
outcomes and use `--validation` for custom validation instructions. Without an
explicit assertion, it suggests one from a newly visible final UI label when
available; otherwise the generated test is disabled until its generic outcome
is replaced. Review the starting state and any omitted input values, then run
`ansight test validate` before relying on the test.

Run only when requested. Add `--trace` when the result needs the full model context, tool payloads, and exportable execution graph rather than lightweight audit metadata:

```sh
ansight test run <repository-root> <test-id> --trace --json
ansight test run-all <repository-root> --trace --json
```

For an existing SDK or external session, attach explicitly:

```sh
ansight test run <repository-root> <test-id> --session-id <live-session-id> --json
```

The attached session determines execution mode; completion releases the execution reservation and leaves its monitor running. To install and launch an SDK-less virtual build, choose device mode explicitly (iOS simulator `.app` or Android `.apk`):

```sh
ansight test run <repository-root> <test-id> --app <build-path> \
  --platform <ios|android> --device-id <device-id> --execution-mode device --json
```

Physical Android device-mode launches require `--platform android` and the exact ADB serial. For physical-iPhone external work, establish a watch with the live-operation skill and attach to its session. Do not combine a session ID with launch options. Capture mode, reasoning mode, `--headless`, and `--trace` are independent choices.

Agentic `test run`, `test run-all`, and the compatibility `test run-inline`
command accept `--reasoning fast|balanced|deep`. Omitting it selects **Fast**, the
normal default for responsive execution. Balanced and Deep express progressively
greater emphasis on reasoning depth; actual model and provider budget come from
the server configuration. Every mode must satisfy the same requested steps and
verification. Preserve the user's choice; do not automatically raise the mode
after a failure. For a requested Balanced run, for example:

```sh
ansight test run <repository-root> <test-id> --reasoning balanced --json
```

This **Reasoning mode** is shared with app execution, replay, App Graph agent
runs, and AI task extraction in Session Replay. The server resolves the model
and provider reasoning effort from the client's configuration and selects the
credential handoff. No `--execution` flag is needed. Leave `--model-transport` at
its default `auto` unless a transport override is requested for diagnostics.
The hidden legacy `--model` override is for diagnostics and cannot be combined
with explicit `--reasoning`. Reasoning is a run option, not a test-schema field.

Test launches show simulator/emulator windows by default. When a local or CI
run should be windowless, add `--headless` to `test run` or `test run-all`; the
flag applies to all selected targets and tool-requested device starts. It is a
CLI option, not a test-schema field. `--json` and `--silent` do not imply it.
iOS skips opening Simulator.app; newly started Android emulators use
`-no-window`. Existing windows, reused sessions, and physical devices are left
alone.

Use `ansight test inspect <run-id> --json` for persisted results and `ansight test export <run-id> <output.zip> --app-id <app-id>` when a detailed trace and its portable session archives must be retained or shared. Treat traces as potentially sensitive.

## Tasks: Deterministic Reuse

Read the current [Workspace Tasks](https://www.ansight.ai/docs/workspace/tasks) and [Task API](https://www.ansight.ai/docs/workspace/task-api-reference) before authoring. A task descriptor must remain statically extractable, its input schema must be rooted at an object, tool calls must be awaited serially, and at least one stable named `check` assertion must establish success. A normal return without assertions is `Inconclusive`.

### Compose Existing Tasks

Before duplicating a stable workflow inside another task, inspect the workspace
catalog with `ansight.tasks.list({ query, feature, maxResults })`. Use the exact
discovered `taskId`, and compose a task only when the candidate's complete
behavior, input schema, and postcondition match the dependency. Do not select a
task merely because its title or a fuzzy match looks plausible.

```ts
type OpenGuideOutput = { guideVisible: boolean };

const catalog = await ansight.tasks.list({
  query: "open area 3D guide",
  feature: "map",
  maxResults: 10
});
const child = catalog.tasks.find(candidate => candidate.taskId === "map.open-guide");
if (!child) throw new Error("The guide task is unavailable.");

const result = await ansight.tasks.run<OpenGuideOutput>({
  taskId: child.taskId,
  input: { area: "Secret Garden" }
});
expect(result.output?.guideVisible, { id: "guide-opened" }).toBe(true);
```

`ansight.tasks.list` and `ansight.tasks.run` need no `hostTools` declaration and
must be awaited serially like other task APIs. Both remain pinned to the
caller's exact repository, App ID, and live session; a child cannot select a
different repository, app, session, or device. Discovery uses the same task
metadata, input-schema, synonym, and conservative typo matching as external
task discovery. `maxResults` defaults to 10 and is capped at 20.

`run` resolves only when the child status is `Passed`. Any other status rejects
the call and fails the parent unless the parent deliberately handles the error;
catch an error only when that failure is an explicitly supported branch. Each
child keeps its own run ID, assertions, timeout, action budget, tool-call audit,
and persisted result. Each discovery or child execution costs one parent action,
but child actions consume only the child's budget. The parent's remaining
deadline caps the complete child call. Child assertions are not merged into the
parent, so the parent must still make its own stable named assertion.

The host rejects direct self-calls and recursive call cycles and permits at most
eight task execution levels in one call chain. Use a shared TypeScript helper
instead of task composition when only pure in-process logic is being reused.

### Ground UI Selectors In Recorded Evidence

For a task derived from a session, every selector must come from the UI evidence
captured inside the selected extraction period. Build a selector inventory from
the visual-tree snapshots and, when text is visible but absent from those trees,
retained screenshot OCR. Do not infer a selector from the task description or
from implementation terminology.

Apply these rules to `nodeId`, `automationId`, `text`, `role`, `type`,
`ancestorAutomationId`, and `action` fields used by `ansight.ui` or
`ansight.keyboard` calls:

- Every literal selector value must match a visible captured UI node. All fields
  in a compound selector must match the same observed node.
- OCR can establish a `text` value only. It is not evidence for an automation
  ID, node ID, role, type, ancestor automation ID, or supported action.
- Logs, telemetry, source code, framework navigation state, handoff text, task
  descriptions, and inferred component names are context, not selector
  evidence. A page, sheet, or view-model class name is not a `type` selector
  unless a captured UI node reports that exact type.
- Prefer an observed automation ID. Otherwise use exact visible text, adding an
  observed role or ancestor only when needed to disambiguate it.
- Keep evidence-derived selectors as inline string literals so they can be
  checked statically. Do not hide them behind variables, input values, spreads,
  computed keys, or helper-returned objects during extraction.
- When no stable observed selector exists, leave a `REVIEW:` diagnostic or omit
  the unsupported step. Never fabricate a plausible selector or silently replace
  it with a coordinate tap.

When the extraction surface offers **Require recorded selectors**, leave it
enabled. Treat its selector-call scan as an additional validation pass: it must
reject an unobserved literal or a dynamically assembled selector that cannot be
proven against the selected period. If that validation is unavailable, enforce
the same invariant manually from `session trees` and retained screenshots. Do
not claim that an extracted task is grounded merely because it compiles.

Discover the exact path-derived task ID and run it only when its whole semantic behavior matches the request:

```sh
ansight task list --app-id <app-id> --repository <repository-root> --json
ansight task run <task-id> --app-id <app-id> --repository <repository-root> --session-id <session-id> --json
```

Treat `Passed` plus its named assertions as completion evidence. Do not replay the same multi-step mutation manually after a terminal task result.

### Execute Through An Existing Interactive Connection

For repeated live actions and task runs, keep one process and resident-host pipe
open instead of spawning a CLI per step:

```sh
ansight app interact --session <session-id> --repository <repository-root> --jsonl
```

Retain the process handle, read its automatic `ready.ui` observation, and send JSON
Lines on stdin:

```json
{"id":"catalog","command":"tasks"}
{"id":"run","command":"task","taskId":"search.find","input":{"query":"Kalymnos"}}
{"id":"sequence","command":"batch","commands":[{"id":"query","command":"type","target":{"automationId":"search-field"},"value":"Kalymnos"},{"id":"submit","command":"tap","target":{"automationId":"search-submit"}}]}
```

Choose IDs and inputs from the returned catalog; these are examples, not tasks
guaranteed to exist. The repository, app and session stay fixed. Task execution
uses the normal engine and returns its status, assertions and tool calls under
`task`; only `Passed` succeeds. Evidence is automatic, including after a failed
task. No capture flag or separate screenshot request is needed.
Each action also returns compact fresh `ui.nodes`; prefer semantic targets and
tree-based verification, opening screenshots for visual checks or missing semantics.
Targeted `type` focuses and replaces text by default (`replaceExisting:false`
appends); untargeted `type` appends. Use observed IDs, not the example IDs above.
Do not manually focus/type before a task that already owns those operations.
Inspect its source once if its full behavior is unclear from the catalog.

Batches contain 1–32 known commands, execute serially, and stop on first failure.
They return ordered `results` and `skippedIds`, preserving evidence per action,
plus top-level final-child `ui`, `screenshot`, and aggregate `timing`. Read child
statuses and final semantic state; inspect intermediate PNGs only when needed.
IDs must be unique within the batch, nested batches and `exit` are forbidden,
and each line is limited to 65536 characters. Do not batch flows that require
deciding targets from intermediate screenshots. A failed batch is not rolled
back; never blindly replay completed or timed-out mutations. Automatic screenshots
settle within a bounded window; `ui_unsettled` retains evidence and stops the batch.
`stable` or `unchanged` pixels are not proof of a task's semantic postcondition.

Task timeout defaults to 120 seconds (`--task-timeout-ms`, maximum 300000); other
commands default to 15 seconds (`--timeout-ms`, maximum 60000). Timeout or
disconnect may interrupt capture and leave execution uncertain. `exit` or stdin
EOF detaches without stopping the app/host. This direct bridge is not `app
execute`, an agentic test, or a new recording session. Use the live-operation
skill for tree-first navigation with screenshot fallback, and keep execution authorization scoped
to the requested task.

`task run` and `repo task run` also accept `--headless` for device starts
requested by permitted host tools. They still require a connected session;
the flag does not authorize or perform an initial app launch for the task.

### Extract A Task From Recorded Behavior

AI task extraction in Session Replay offers **Reasoning mode: Fast / Balanced /
Deep**, defaulting to Fast and using the same client configuration as agentic
runs. Preserve the user's selection. The CLI `task extract` command below
generates a deterministic draft from recorded events and does not use a model;
do not add `--reasoning` to it. Deterministic `task run` and `repo task run` also
have no reasoning mode.

Use extraction when an existing session contains the exact deterministic behavior to preserve. Inspect its time bounds, touches, and visual-tree targets first:

```sh
ansight session show <session-id> --json
ansight session touches <session-id> --json
ansight session trees <session-id> --json
ansight task extract <session-id> --start <timestamp-or-offset> --end <timestamp-or-offset> --workspace <repository-root> --title <title> --json
```

Keep the extraction period narrow enough that the selector inventory represents
the behavior being preserved. Inspect representative beginning, middle, and end
snapshots when the period has many trees or screenshots. Treat the generated
TypeScript as a draft. Review every `REVIEW:` comment, define the required
starting state, apply the recorded-evidence selector rules above, and replace the
generated UI-stability check with a meaningful product outcome assertion.
Manually author unsupported long presses or multi-touch gestures; a tap without
a stable selector remains unresolved. Type-check, rediscover, and run the task
against a live session with its declared capabilities before relying on it. External recordings have no user touch stream; extract only behavior established by retained automation/evidence and leave unsupported steps for review.

### Debug Or Repair Only When In Scope

Use this procedure only for requested debugging or repair, or to validate work
authored in this request. For execution-only requests, report the terminal result
and stop. Preserve the failed task result and its evidence before editing or replaying.
Start with the first failed tool call rather than the final exception or a nearby
application log:

1. Map the failed tool name and occurrence number to the corresponding source
   call, and record its complete selector and state constraints.
2. Use the tool-call timestamps to bracket the task run. Inspect UI evidence
   after the preceding successful action and through the failed call.
3. Compare every visual-tree source available at those snapshots. Framework,
   native, and accessibility trees can expose different nodes. If a node exists
   only in the native tree, that proves the native node—not a missing framework
   class or inferred component type.
4. Inspect retained screenshot OCR when visible text is missing from the trees.
   Keep the distinction between OCR text and structured selector fields.
5. Search logs only to determine whether a failed selector came from
   implementation terminology. A string appearing only in logs is affirmative
   evidence that it was not grounded as a UI selector.
6. Compare the UI before and after the preceding action and propose replacements
   only from nodes or OCR text that newly appeared or became relevant there.

Classify the result before changing the task:

| Evidence | Likely diagnosis | Repair direction |
| --- | --- | --- |
| Selector never appears in any tree or OCR result | Invented or stale selector | Replace it with an observed selector, or leave the step unresolved. |
| Selector appears only in logs, source, or framework names | Implementation string used as UI evidence | Remove it; use captured UI text or a captured automation ID. |
| Matching node exists, but not with the requested visibility, enabled state, role, ancestor, or action | State constraint or compound-selector mismatch | Correct only the constraint contradicted by the captured node. |
| Matching node appears after the preceding action but after the wait timeout | Timing or transition problem | Wait on the observed postcondition and use a bounded timeout supported by replay evidence. |
| Several nodes match | Ambiguous selector | Add an observed role, ancestor, or index only when the same evidence proves it. |
| UI is visible in a screenshot or native tree but absent from a framework tree | Evidence-layer coverage gap | Use an exact selector exposed by an available tree, or OCR-backed visible text; do not invent the missing framework node. |

In Session Replay task extraction, run the draft against a connected session and
use **Debug why this task failed** when it is offered. Its failed-call mapping,
selector-presence finding, and newly visible selector suggestions are evidence
leads; verify the proposed selector against the retained tree or OCR provenance
before accepting it. Outside that UI, use the `task run --json` result and the
session evidence commands above. Do not invent an `ansight task debug` command.

After repairing the first supported cause, restore a known starting state and
run the task again. Stop after a terminal result; do not blindly repeat a timed
out or partially completed mutation. A passing run must still contain the named
product-outcome assertion, not merely successful tool calls.

## Triggers: Event-Driven Evidence

Read the current [Workspace Triggers](https://www.ansight.ai/docs/workspace/triggers) and [Trigger API](https://www.ansight.ai/docs/workspace/trigger-api-reference) before authoring. Keep the descriptor statically extractable, select one exact normalized event kind, and use declarative ANDed conditions. A handler may return no action or one bounded app action; it must not call unrelated host tools.

Inspect without executing code, then connect only with authorization:

```sh
ansight repo automation inspect <app-id> <repository-root> --json
ansight repo automation connect <app-id> <repository-root> --json
ansight repo automation list --json
ansight repo automation runs <app-id> --json
ansight repo automation disconnect <app-id> --json
```

For external captures, use version 2 triggers with observed native-log events and supported host actions such as screenshots, file capture, or annotations. App actions need an SDK provider. Declare the action capability and verify the selected session supports it.

Connected triggers are trusted local code and persist with the linked workspace across resident-host restarts. External edits are not hot-reloaded; inspect again and reconnect after changing definitions. A missing run is not evidence of success.

## Trends: Deterministic Metrics And Regression Evidence

An Ansight Trends definition starts and ends at matching SDK/host events or captured log messages. Its inline `span` gives a meaningful app operation—such as launch, login, search, synchronization, or loading one item—a stable identity and time boundary for its metrics.

Inspect event labels or native log messages in captured evidence before defining a span; never invent them. Treat SDK event labels as a versioned instrumentation contract. For external sessions, log selectors need the observed `streamId` and `message`, with optional tag/source/priority/process filters; iOS simulator uses `apple-unified-log`, Android uses `android-logcat`. Physical iPhone external capture has no native app logs. Host spans are another option when declared and supported. Match only available channels: iOS simulator FPS is unavailable, Android FPS is conditional, and physical-iPhone Instruments samples may arrive after export. Keep SDK and external provider baselines separate, and report missing boundaries or samples as inconclusive.

A session can contain several completed instances of the same operation. Choose `firstCompleted`, `lastCompleted`, `exactlyOne`, or `all` deliberately for the intended analysis. When paired start and end events carry the same non-empty `details`, Ansight keeps that value as a group so repeated operations such as loading “Secret Garden” and “Seaside” form separate metric and regression series. Set `maximumDurationMs` high enough for a realistic slow run but low enough to prevent an unrelated later end event from closing an abandoned start.

A Trends definition selects telemetry inside each chosen span, calculates deterministic `metrics`, and applies each metric's `budget`. Keep behavioral assertions in the test prompt or optional validation block and numeric trend rules in Trends definitions.

An optional `regression` belongs on the metric it monitors. Use [Trends](https://www.ansight.ai/docs/workspace/trends) for reference, regression, confirmation, and comparable-series rules. Inspect stored results with:

```sh
ansight trends history --app-id <app-id> --json
```

Changing a regression policy does not rewrite prior decisions automatically. Preview with `ansight trends rebuild --app-id <app-id> --dry-run --json`; apply the rebuild only when the user asked to replace stored decisions under the current rules.

Validation proves definitions and references are coherent. Only a finalized registered session produces metrics and historical comparison decisions.

## Sanitizers: Privacy-Safe Exports

Read [Sanitizers](https://www.ansight.ai/docs/workspace/sanitizers) before implementing a privacy policy. Use the generated typed sanitizer, return an edited item to retain it or `null` to remove it, fail closed for screenshots that neither OCR nor visual-tree text can inspect, and make an explicit keep-or-remove decision for binary artifacts.

Type-check the sanitizer, create a separate archive through `ansight session sanitize` or `ansight session export --sanitizer`, and inspect representative logs, network metadata, screenshots, trees, annotations, analyses, text artifacts, and binary artifacts in the result before sharing it. Never describe an uninspected sanitized archive as safe.

## Validate The Complete Workspace

Run only the checks whose files and configurations exist:

```sh
ansight test validate <repository-root> --json
npx tsc -p ansight/tasks/tsconfig.json
npx tsc -p ansight/triggers/tsconfig.json
npx tsc -p ansight/sanitizers/tsconfig.json
```

Resolve every validation warning. The runtime strips TypeScript types but does not type-check modules before execution. Re-run discovery after external edits, and reconnect triggers after changing them. Treat the installed host as authoritative when checked-in schemas or declarations disagree with it.

## Report Results

Report the workspace root, App ID, capture mode and capability gaps, registration state, component type, path-derived ID, scaffold or files changed, validation and discovery performed, and whether execution was requested. For tests, report the outcome and whether `--trace` was enabled. For tasks, report terminal status and named assertions. For triggers, report connection and run state. For Trends, distinguish validation from an actual runtime evaluation. For sanitizers, report the output archive and inspection performed without claiming that the original session was modified.
