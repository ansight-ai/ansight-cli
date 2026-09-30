---
name: ansight-operate-live-app
description: Operate an app through an Ansight SDK or external device session while preserving before-and-after evidence. Use to choose a capture mode, monitor an installed app without an SDK, boot a target, launch or stop an app, interact, or verify visible state; do not use merely to prepare the CLI, analyze a recording, or call app-internal remote tools.
---

# Operate A Live App With Ansight

Establish one exact live session, observe before acting, perform the smallest authorized interaction, and verify the outcome from fresh evidence. Use `--json` for discovery and evidence commands, and keep the selected session ID explicit after resolution.

## Prerequisites And Routing

Live operation supports SDK sessions and external device sessions. Resolve the capture mode before treating a missing SDK as a blocker.

- If the `ansight` executable, resident host, or device dependencies are not ready, report the missing prerequisite. Use setup only within the requested scope: `https://www.ansight.ai/skills/ansight-cli-setup.md`.
- If the requested work needs app-internal tools, resolve an SDK session and follow the Ansight Remote App Tools skill. SDK installation is a separate step, performed only when in scope: `https://www.ansight.ai/skills/ansight-install.md`.
- Keep SDK enrollment, capture, and remote capabilities excluded from protected builds unless the app's documented policy explicitly permits them. External capture can inspect an installed store build without adding an SDK.

Do not install or modify the SDK merely because no session is currently connected. First distinguish a missing integration from an app that is stopped, using the wrong build configuration, or temporarily disconnected.

## Choose The Capture Mode

Preserve the user's explicit choice and reuse a matching live session when its capabilities satisfy the request. If no choice is given, explain the relevant options and ask when the tradeoff affects app changes, device setup, or required evidence. A request to inspect without code changes selects external capture; an explicit SDK installation request selects SDK capture. Do not ask again after a mode has been selected.

| Mode | Choose it for | Requirements and limits |
| --- | --- | --- |
| External · simulator or emulator | Quick capture and visible UI automation without an SDK or rebuild | Installed app, booted virtual device, host, and platform tools. Screenshots, accessibility UI, native logs, available metrics, and accessible sandbox files. iOS simulator FPS is unavailable; Android FPS depends on renderer support. |
| External · physical iOS or Android | Inspect an installed phone app, including a store build, without app changes | Exact device ID. iPhone: Mac, Xcode, trusted unlocked phone, Developer Mode, UI Automation, Appium/XCUITest and signed WebDriverAgent. Android: authorized ADB and USB debugging. Backgrounding ends the recording. |
| SDK · physical or virtual | Rich app evidence and in-process framework or domain tools | Initialized SDK in a trusted build and host connection. Virtual devices register automatically; physical devices use a one-time pairing QR. Features depend on enabled packages and policy. |

External UI automation can send taps and gestures; recording the user's touches, instrumented network requests, framework internals, custom app events, and remote app tools requires the SDK. Physical iPhone external capture provides screens and accessibility UI; native app logs and third-party sandbox files are unavailable. Optional Instruments can add CPU/memory after export. Physical Android also provides Logcat and available metrics; Android private files need a debuggable app with `run-as` or an already provisioned root ADB daemon.

An SDK session itself needs neither Appium nor iOS Developer Mode nor Android USB debugging. Deployment, device input, and optional profiling have their own requirements. Capture source is reported as `sdk` or `device`; physical and virtual external captures both use `device`. Inspect session capabilities and their providers rather than inferring availability from the mode alone.

For external setup, watch lifecycle, and bounded device execution, read [External capture](references/external-capture.md). The public comparison is [Capture modes](https://www.ansight.ai/docs/capture-modes).

## Keep The Lifecycle Layers Separate

| Layer | Start or attach | Stop | Meaning |
| --- | --- | --- | --- |
| Resident host | `ansight host status --json`; `ansight host run` | `ansight host stop` | Local capture and control service shared by sessions |
| Device | `ansight device list --json`; `ansight device start <platform> <device-id>` | `ansight device shutdown <platform> <device-id>` | Simulator, emulator, or physical target |
| App process | `ansight device launch <platform> <device-id> <app-id>` | `ansight device terminate <platform> <device-id> <app-id>` | Installed app instance |
| SDK session | The SDK connects when the enabled app reaches the host | `ansight session disconnect <session-id>` | App evidence stream retained as a recording after disconnect |
| External watch | `ansight app watch add <app-id> ...`; host observes the running app | `ansight app watch disable <watch-id>` | Persistent monitor; each matching device/run gets a retained session |

There is no separate `ansight session start` command. Start the host and app, establish the SDK connection or external watch, then discover its session. `session delete` removes retained data; it is not a stop command.

Do not stop a host, device, or app that was already running unless the user asked. A resident host can be shared by other work.

## Choose Device Window Behavior

Device launches show simulator/emulator windows by default. When the user wants
windowless operation, pass `--headless` on the command that starts the device:

```sh
ansight device start ios <simulator-id> --headless --json
ansight device start android <avd-name> --headless --json
```

The same flag applies to automatic launches by `app execute`, `app-graph explore
--launch`, replay, tests, and profiling, plus tool-requested starts during a
command. iOS skips opening Simulator.app; new Android emulators use `-no-window`.
It does not close existing windows, restart running emulators, or affect physical
devices. Reusing a connected session leaves its window state unchanged.
`--json`, `--silent`, and CI do not imply headless mode.

## Establish The Exact Session

1. Check the host:

   ```sh
   ansight host status --json
   ```

   If the CLI or workstation is not configured, report the blocker; use the Ansight CLI Setup skill only when setup is in scope. If no host is running and live control is required, run `ansight host run` in a durable terminal and leave it attached.

2. Inspect devices only when the app must be launched or the user named a target:

   ```sh
   ansight device list --all --json
   ```

   Start only the exact simulator or emulator needed. A physical iOS target is verified rather than booted.

3. For external capture, configure or reuse the watch described in [External capture](references/external-capture.md). Launch the selected installed app when it is not already running:

   ```sh
   ansight device launch <ios|android> <device-id> <app-id> --json
   ```

4. Discover and select the live session:

   ```sh
   ansight session list --connected --app-id <app-id> --limit 20 --json
   ansight session show <session-id> --json
   ```

   Match the App ID, device profile, platform, capture source, and connection state. For an external watch, cross-check its active session ID in `ansight app watch list --json`. If multiple sessions still match, do not choose the newest silently; use the user's supplied device or ask which instance is in scope.
   Follow `nextCursor` with `--cursor` only when the first filtered page does
   not contain the intended session, and keep the same filters on later pages.

5. Record the session ID and initial observation timestamp or evidence IDs.

## Fast Direct Exploration

For interactive exploration or human-style navigation, prefer one persistent process:

```sh
ansight app interact --session <session-id> --jsonl
```

Retain its process/PTY handle and write JSON Lines to stdin; do not start a new
CLI process for every gesture. The resident-host pipe remains open. This is direct
control by the current agent, not `app execute` or a delegated agent.

The initial `ready` response and every action include a compact `ui` observation
from fresh accessibility/visual trees, plus an automatically retained screenshot.
Prefer `ui.nodes` to choose and verify semantic targets; no separate tree or
screenshot request is needed. Use exact automation IDs, or exact text narrowed by
role and `ancestorAutomationId`. The bridge resolves targets freshly and refuses
missing or ambiguous matches without input:

```json
{"id":"query","command":"type","target":{"automationId":"search-field"},"value":"Kalymnos"}
{"id":"select","command":"tap","target":{"text":"Kalymnos","role":"button"}}
```

These IDs are examples; use those actually observed. Targeted `type` focuses and
replaces the field by default; `replaceExisting:false` appends. Untargeted `type`
appends to the focused field. Never mix a target and coordinates. Trees are bounded
to 128 visible semantic nodes and 256 characters per text field; `ui.truncated`
means absence is not proof that a target does not exist. Use focused semantic
inspection below for omitted details or longer exact selectors.

View `screenshot.artifactPath` for custom-rendered controls, visual questions,
missing/insufficient tree semantics, or a mismatch between tree and expected state.
The PNG is captured from the selected SDK or external session. Coordinates are a fallback
normalized to that screenshot (0–1):

```json
{"id":"1","command":"tap","x":0.5,"y":0.8}
{"id":"2","command":"swipe","x":0.5,"y":0.8,"endX":0.5,"endY":0.2}
{"id":"3","command":"pinch","x":0.5,"y":0.5,"scale":1.5}
{"id":"4","command":"type","value":"Kalymnos"}
{"id":"5","command":"back"}
```

Read each result before deciding an unknown next target. `snapshot` optionally
refreshes asynchronous state; do not append it routinely after an action or task.
Evidence capture is implicit. Screenshots use bounded visual settling (300 ms
quietness, a 900 ms grace for unchanged screens, and a 2 s sampling window).
`screenshot.settling.status` is `stable`, `unchanged`, or `timed_out`; quiet pixels
do not prove a network request or semantic goal completed. `ui_unsettled` stops a
batch and retains the latest screenshot; inspect before continuing, never replay
the action just to obtain evidence.
`previousScreenshot` is the last observed frame, not a claim that nothing changed
since then. Reobserve when the app may have changed independently.

For a known sequence that needs no intermediate decisions, send one batch instead
of paying a tool round trip for every action:

```json
{"id":"search","command":"batch","commands":[{"id":"focus","command":"tap","x":0.5,"y":0.8},{"id":"query","command":"type","value":"Kalymnos"}]}
```

Batches run in order, with automatic evidence for every action. The response has
ordered `results` and `skippedIds`; execution stops at the first failure, including
missing evidence or a timeout. Earlier actions are not rolled back. Never replay
the whole batch after partial completion. Use 1–32 commands with unique IDs
(including the batch ID), no nested batches or `exit`, and at most 65536 characters
per line. Check child statuses, then use the top-level `ui` and `screenshot` for
the final executed child's state. Inspect intermediate screenshots only when a
failure, visual question, or missing semantic evidence requires it. Top-level
`timing` separates summed input/capture time from host batch wall time; it excludes
the agent's reasoning and tool round trips.

To reuse maintained tasks, pin the trusted repository when opening the process:

```sh
ansight app interact --session <session-id> --repository <repository-root> --jsonl
```

```json
{"id":"catalog","command":"tasks"}
{"id":"run","command":"task","taskId":"search.find","input":{"query":"Kalymnos"}}
```

Use the returned catalog's exact task ID and input schema. `task` uses the existing
task engine in the same app/session, captures resulting evidence automatically,
and returns `task.status`, named assertions and tool calls. Only `Passed` is
successful; a normal return without assertions is `Inconclusive`. Task commands
can be batch children. Run only behavior the user authorized; opening the bridge
does not authorize every available task. Default action timeout is 15 seconds;
task timeout is 120 seconds (`--timeout-ms` up to 60000 and `--task-timeout-ms` up
to 300000). A timeout/disconnect can leave execution uncertain and fresh evidence
unavailable; observe before deciding how to recover.

Before manually beginning a maintained multi-step flow, inspect the matching task's
complete behavior once (including its source when the catalog is ambiguous).
If it enters a query and selects a result, invoke it with the query directly—do
not focus/type first and then have the task repeat those steps. If a user explicitly
wants manual exploration, keep that route instead of running the same task afterward.

On failure, report the error, available evidence, and partial effects. Allow one
evidence-supported correction or transient retry per requested operation, only
when it cannot duplicate input. If execution is uncertain, observe once and stop
if still unresolved. Diagnose or repair only when requested. An `exit` command
or stdin EOF detaches without stopping the app or host. Ctrl+C cancels the connection.
If the installed CLI/host does not support `app interact`, use the semantic workflow
below and report the limitation; do not restart a shared host without authorization.

Use focused semantic inspection below for assertions or details omitted from `ui`.
Do not repeatedly request full trees or discover workspace tasks merely
to navigate quickly when the user requested direct exploration.

## Reuse Existing Workspace Behavior

Before manually performing a multi-step flow, make one focused repository-task search when a linked workspace exists and the request resembles maintained repeatable behavior:

When already connected with `--repository`, send `tasks` on that connection and
use `task` to execute the selected behavior. Do not open a separate CLI process.
Otherwise use:

```sh
ansight task list --app-id <app-id> --repository <repository-root> --json
```

Run a returned task only when its complete behavior and input schema exactly match the request. Treat a passed task and its named assertions as authoritative evidence; do not replay a failed, rejected, or inconclusive task manually and risk duplicating state changes. Skip task discovery for a simple one-step interaction or when no repository workspace is linked. Run a whole workspace test only when the user requested that scenario.

## Observe Before Acting

For precise semantic workflows (as distinct from screenshot-driven exploration), start with:

```sh
ansight ui snapshot --session <session-id> --json
ansight ui find --session <session-id> --automation-id <id> --json
```

Use the narrowest stable selector available:

1. exact `--automation-id`;
2. exact text plus role;
3. an ancestor-qualified selector;
4. `--index` only after a fresh find proves the intended match.

All supplied selector fields constrain the same node. Treat node identifiers and result indexes as observation-scoped. Re-observe after navigation, dialogs, list changes, or other material state transitions.

If in-process framework or domain state would answer the question more directly, follow the Ansight Remote App Tools skill instead of guessing from the UI.

## Interact And Capture Evidence

Use one small action at a time:

```sh
ansight ui tap --session <session-id> --automation-id <id> --json
ansight ui type --session <session-id> --automation-id <id> --value <text> --json
ansight ui swipe --session <session-id> --ancestor <id> --direction up --length 0.6 --json
ansight ui pinch --session <session-id> --automation-id <id> --scale 1.8 --json
ansight ui back --session <session-id> --json
```

UI actions use real device input and return structured before-and-after evidence. The persistent bridge is the tree-first fast path; these one-shot commands provide richer selectors and assertions. Raw `ansight input` is a fallback when these surfaces have a concrete capability gap.

Do not mutate app state merely to make an assertion pass. A validation-only request authorizes observation, not repair.

## Record Bounded Executions In Depth

`app execute`, `app-graph run`, and `app-graph explore` accept
`--reasoning fast|balanced|deep`. Omitting it selects **Fast**, the normal default
for responsive execution. Balanced and Deep express progressively greater
emphasis on reasoning depth; actual model and provider budget come from the
server configuration. Every mode must satisfy the same requested steps and
verification. Preserve the user's selected mode; do not automatically raise it
after a failure.

This is the same **Reasoning mode** used by agentic test runs, session replay, and
AI task extraction in Session Replay. The server resolves the model and provider
reasoning effort from the client's configuration and selects the credential
handoff. Leave `--model-transport` at its default `auto` unless a transport override
is requested for diagnostics; no `--execution` flag is needed. The hidden legacy
`--model` override is for diagnostics and cannot be combined with explicit
`--reasoning`.

For example, when the user requests Deep:

```sh
ansight app execute <session-id> --reasoning deep --prompt "<goal>" --json
```

App Graph operations require SDK mode. Plain `app execute` also supports an external session; supplying its session ID selects device mode without a launch-mode flag.

When the user needs a replayable or exportable account of an agent-driven run, add `--trace` to the command that performs it:

```sh
ansight app execute <session-id> --prompt "<goal>" --trace --json
ansight app execute <session-id> --prompt "<goal>" --app-graph --trace --json
ansight app-graph run <graph-id> <session-id> --trace --json
ansight app-graph explore <app-id> --trace --json
```

For `app execute`, `--trace` retains the full model context, assistant text, tool payloads, App Graph plan details, and exportable execution graph. App Graph `run` and `explore` use it to retain the full exportable agent trace. Without the flag, Ansight deliberately stores only lightweight audit metadata such as status, timing, token and call counts, and payload hashes.

Use `--trace` when the run must be inspected in depth, replayed in the local player, exported, or used as detailed evaluation evidence. Treat the resulting trace as potentially sensitive because it can contain model context and tool payload bodies; do not enable it solely to obtain ordinary status or timing evidence.

## Wait And Verify

Wait for a specific asynchronous state instead of sleeping:

```sh
ansight ui wait --session <session-id> --automation-id <id> --condition visible --timeout-ms 15000 --json
```

Then verify the requested postcondition from fresh state:

```sh
ansight ui assert --session <session-id> --automation-id <id> --expected-visible true --json
ansight ui snapshot --session <session-id> --json
```

An action response proves that input was attempted. It does not prove the requested outcome. Use an assertion, focused observation, or resulting session evidence for that conclusion.

## End Only The Intended Layer

- When the user wants an `app execute` run to close the app afterward, add `--close-app-on-completion`. It closes only that app process after success, failure, or cancellation, including when reusing an existing session. Multi-device runs close each selected app instance. The device, its window, and the resident host stay running. The default is to leave the app open; `--headless` controls windowless startup independently. A cleanup warning means the app may still be running; do not claim it closed without verification.
- End an SDK stream while retaining the recording: `ansight session disconnect <session-id> --json`.
- Stop future external capture and end its active recording: `ansight app watch disable <watch-id> --json`. Watches persist across host restarts; end or disable only the exact monitor the user asked to stop or this workflow exclusively created. See [External capture](references/external-capture.md) before changing a monitor that covers several devices.
- Stop the app process: `ansight device terminate <platform> <device-id> <app-id> --json`.
- Shut down a virtual target only when the user requested it or this workflow exclusively started it.
- Stop the resident host only when explicitly requested or when this workflow started a dedicated host and no other session depends on it.

The app may reconnect while its SDK or external watch remains active. After a requested stop, confirm with `ansight session list --connected --app-id <app-id> --limit 20 --json` and, for external capture, `ansight app watch list --json`.

## Report The Evidence Trail

Report the selected App ID, device, capture mode, and session; initial observation; each state-changing CLI command; fresh verification; important timestamps or evidence IDs; and any fallback outside Ansight. Name material capability gaps and whether a persistent watch remains enabled. Distinguish observed state, executed action, and inferred explanation.
