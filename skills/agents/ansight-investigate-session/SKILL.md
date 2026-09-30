---
name: ansight-investigate-session
description: Investigate one live or recorded Ansight session through retained evidence. Use to bracket and correlate logs, network, telemetry, screenshots, trees, touches, artifacts, and annotations or create an evidence slice; do not use to prepare the workstation, drive the live UI, or write annotations.
---

# Investigate An Ansight Session

Build a timestamped evidence trail from one exact session before reading source code or proposing a cause. Report an unresolved cause when evidence is missing or no concrete lead remains; do not repair the app or diagnostic infrastructure.

Use `--json` throughout. Keep timestamps in UTC and preserve source IDs such as frame IDs, snapshot IDs, request IDs, annotation IDs, channel IDs, and artifact snapshot IDs.

## Prerequisites And Routing

Recorded-session investigation requires the Ansight CLI or another exposed Ansight diagnostic surface and a session available from local history or an imported archive. The target app does not need to remain installed, connected, or currently integrated with the SDK when retained evidence is sufficient.

- Live investigations can use SDK or external device sessions. Use `https://www.ansight.ai/skills/agents/ansight-operate-live-app/SKILL.md` to choose the capture mode and establish the exact session when new evidence is needed.
- If the selected mode cannot supply the requested evidence, report that specific gap. App-internal tools, instrumented network evidence, and recorded user touches need SDK features; install them only when in scope: `https://www.ansight.ai/skills/ansight-install.md`.
- If the CLI or resident host cannot access local or imported sessions, report the blocker. Use setup only within the requested scope: `https://www.ansight.ai/skills/ansight-cli-setup.md`.

Do not require or install the SDK merely to inspect an existing recorded or imported session.

## Interpret Capture Coverage

Record capture source (`sdk` or external `device`), platform, physical or virtual target, providers, and enabled streams before forming a hypothesis. External captures support screenshots and accessibility UI; native logs, metrics, and sandbox access vary by platform. Physical iPhone external capture has no native app logs or third-party sandbox files; optional Instruments CPU/memory may arrive after export. Android uses Logcat and available metrics, with private files gated by `run-as` or an already provisioned root ADB daemon. iOS simulator FPS is unavailable; Android renderer FPS is conditional.

External capture does not record user touches or instrumented network requests. Missing SDK-only evidence is a capture limit, not proof that no input or request occurred. Automation command evidence establishes sent input separately from user touch recording. Physical-phone watches end on backgrounding; virtual watches follow process lifetime and can show another app on the display. Attribute conclusions only to the observed app and interval.

## Select The Session

Start with focused discovery. When the user supplied an exact session ID, query
the lightweight session-summary index and confirm that the returned
`sessionId` is an exact match:

```sh
ansight session list --app-id <app-id> --from <utc> --to <utc> --limit 20 --json
ansight session list --search <session-id> --limit 1 --json
```

Do not use `ansight session show <session-id> --json` for discovery. Its JSON
response can materialize the session's retained logs, metrics, and visual trees,
which is unnecessarily large for agent context. Use the summary index above,
then query only the evidence surface needed for the investigation.

Add `--connected` only when the task requires a live session. Use app identity, device profile, capture time, tags, retained evidence totals, and connection state to choose. Do not silently choose among multiple plausible sessions.
The time filters select sessions whose capture intervals overlap the requested
range. Follow `nextCursor` with `--cursor` only when the first filtered page is
insufficient, preserving the same filters between pages.

Read existing annotations early because they may already define the relevant moment or region:

```sh
ansight session annotations <session-id> --json
```

## Bracket The Investigation

Define the smallest interval that can answer the question. Prefer, in order:

1. an existing annotation's `startUtc` and `endUtc`;
2. a stable app event, log, request, touch, or screenshot timestamp near the reproduction;
3. a narrow interval around a telemetry detector result;
4. the whole session only when no reliable anchor exists.

State the interval before correlating evidence. Expand it deliberately when a cause may precede the visible symptom.

## Reuse Workspace Evidence Boundaries

When a linked `ansight/` workspace exists, use its maintained definitions as evidence leads: Trends results can provide established observation spans, telemetry budgets, and regression comparisons, while triggers can explain automatically captured artifacts. Confirm that each definition's App ID, event labels, inputs, and session match the investigation before relying on it. Route any request to create or change these definitions to the Ansight Workspace Automation skill.

## Build The Evidence Stack

Use the surfaces that match the hypothesis rather than dumping every retained item.

### Logs And Network

```sh
ansight session logs <session-id> --start <utc> --end <utc> --minimum-priority warning --json
ansight session logs <session-id> --start <utc> --end <utc> --contains <text> --json
ansight session network <session-id> --start <utc> --end <utc> --failed --json
ansight session network <session-id> --start <utc> --end <utc> --host <host> --json
```

Begin with high-signal filters, then widen severity, streams, sources, tags, status classes, or text only as needed. Include network bodies only when the request needs them and captured content is safe to inspect.

### Telemetry

Run the built-in detectors as leads:

```sh
ansight session telemetry analyze <session-id> --kind all --json
```

Use `--kind fps-drop` or `--kind memory-spike` for a focused check. Threshold flags change detector sensitivity; they do not establish a product requirement. Use `--fail-on-detection` only when exit code `11` should mean a finding was detected.

Inspect channel metadata and samples:

```sh
ansight session metrics <session-id> --json
ansight session metrics <session-id> --channel <channel-id> --limit <count> --json
```

The metrics command selects by channel and newest-sample limit, not by time range. Choose a limit that includes the investigation interval, then filter and correlate the returned UTC sample timestamps. Never imply that `--limit` isolated a time window.

### Visual And Interaction Evidence

```sh
ansight session images <session-id> --json
ansight session trees <session-id> --json
ansight session touches <session-id> --json
```

Join screenshots and visual trees through `screenshotFrameId` and nearby capture timestamps. Use touches to establish what input occurred, not whether the intended postcondition succeeded. Export one relevant frame when visual inspection is needed:

```sh
ansight session screenshot export <session-id> --frame-id <frame-id> --output <path>
```

### Artifacts

```sh
ansight session artifacts <session-id> --json
ansight session artifact export <session-id> --snapshot-id <id> --path <artifact-path> --output <path>
```

Inspect only artifacts relevant to the hypothesis. Distinguish SDK app-supplied diagnostics from host-captured sandbox copies or native traces, and preserve their provider, snapshot ID, capture time, and path in the report.

## Slice Evidence Safely

| Need | Preferred action |
| --- | --- |
| Inspect a narrow log or network interval | Use `--start` and `--end`; do not create another session |
| Correlate all evidence surfaces for a durable interval | Create a derived session with `session extract` |
| Reuse an annotation's exact interval | Extract by annotation ID |
| Remove evidence from the original session | Use `session trim` only with explicit authorization |

Create a non-destructive derived slice only when the user asks to retain, hand off, replay, or repeatedly inspect it:

```sh
ansight session extract <session-id> --start <utc> --end <utc> --name <name> --json
ansight session extract <session-id> --annotation <annotation-id> --name <name> --json
```

The new session is a separate local capture. Record its returned session ID. Do not call `session trim` during ordinary investigation: `cut` and `keep` mutate the existing session timeline.

## Correlate Before Concluding

For each candidate explanation:

1. Identify the earliest supporting event.
2. Check what changed immediately before and after it across an independent surface when available.
3. Distinguish correlation from causation.
4. Check for contradictory evidence and normal control periods.
5. State capture gaps, truncation, missing channels, sparse screenshots, or absent visual trees.

Prefer runtime evidence over a source-only explanation. Read source after the observed behavior identifies a component, handler, request, or state transition to inspect.

## Report A Reproducible Evidence Trail

Report the selected session, capture mode, providers, and why it matched; UTC interval and anchor; material commands and filters; timestamped observations with IDs; strongest supported explanation and confidence; contradictory or missing evidence; any derived slice and its session ID; and the smallest next verification step.

Do not present detector output, an input attempt, or a single coincident log as proof by itself.
