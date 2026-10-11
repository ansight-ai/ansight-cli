---
name: ansight-annotate-session
description: Add, update, verify, or remove timeline and UI-anchored annotations on one Ansight session. Use for requested review metadata, screenshot geometry, or visual-tree target binding; do not use merely to read annotations, investigate evidence, or operate the live app.
---

# Annotate An Ansight Session

Attach concise review metadata to one exact session. Keep the observed fact in the label, supporting context in notes, and inference explicitly qualified.

## Mark Proof Of Work

Use native Ansight annotations only to emphasize a specific area of interest that directly relates to the feature, bug, or task worked on. A timeline marker or range should identify a relevant moment or interval; screenshot/UI geometry should draw attention to the relevant region. Add or highlight an annotation only when that emphasis helps the user understand the work or its evidence. Reuse the captured run and suitable existing annotations. If nothing needs emphasis, present the replay without adding annotations.

Before marking anything, identify which part of the requested work it illustrates. Avoid annotating routine navigation, unrelated observations, or every passed check merely to decorate the replay or produce a completion checklist. A failure or unresolved check warrants a marker only when it concerns the task and has a specific captured moment or region worth highlighting.

Labels should describe the observed result, such as "Search results collapse by type"; notes should connect it to the requested change, reproduction step, assertion, and evidence IDs. Anchor every marker to actual capture timestamps and every shape to an inspected frame. Touches establish input, while a fresh screenshot, assertion, or other retained evidence establishes the outcome. Do not manufacture a before/after comparison when only the result was recorded.

Use the annotation commands and verification below when a relevant area needs emphasis, preserving existing user annotations. Respect a user's read-only or no-annotation constraint. A read-only investigation should reuse relevant existing markers unless annotation writes were also requested. Do not upload or publish a capture just to provide proof.

## Prerequisites And Routing

CLI annotation requires the Ansight CLI and a session available from local history or an imported archive. The app does not need to remain installed or connected, and its SDK does not need to be present, when annotating retained evidence.

- A UI-anchored annotation also requires a captured screenshot frame; a semantic target requires a corresponding persisted visual-tree snapshot.
- Direct diagnostic annotation tools such as `ansight_inject_annotation` require an available diagnostic tool connection and a resolvable session. A live-only tool path also requires an initialized SDK session.
- If new live evidence must first be captured, use `https://www.ansight.ai/skills/agents/ansight-operate-live-app/SKILL.md` to establish an SDK or external device session. Report gaps in the chosen mode without treating a missing SDK as a blanket blocker.
- If the CLI cannot access the session, report the blocker. Use setup only within the requested scope: `https://www.ansight.ai/skills/ansight-cli-setup.md`.

Do not install or reconnect the SDK solely to add metadata to an existing recorded or imported session.

SDK and external recordings both support CLI annotations. Bind geometry and semantic targets only to screenshots and trees actually retained by that capture. An external accessibility node does not establish framework-internal state or a recorded user touch.

Annotation writes change retained session metadata. Resolve the session and read existing annotations before creating or replacing one:

```sh
ansight session show <session-id> --json
ansight session annotations <session-id> --json
```

Use a stable, meaningful annotation ID when the note may be updated. A CLI upsert with an existing ID replaces the stored annotation, so preserve any geometry, target, evidence, custom data, and capture metadata that should remain.

## Add A Timeline Annotation With The CLI

Use CLI fields for a point-in-time or range annotation:

```sh
ansight session annotation upsert <session-id> \
  --annotation-id <stable-id> \
  --label <label> \
  --notes <notes> \
  --source agent \
  --start <utc> \
  --end <utc> \
  --json
```

Omit `--end` for an instantaneous marker. Use ISO-8601 UTC timestamps taken from session evidence. Do not manufacture precision beyond the evidence that established the moment.

## Ground A UI Annotation

A UI annotation is still time-based, but also anchors geometry to a captured screenshot frame. Establish the frame before writing:

```sh
ansight session images <session-id> --json
ansight session screenshot export <session-id> --frame-id <frame-id> --output <path>
ansight session trees <session-id> --json
```

Choose the frame that displays the observed issue. If the annotation targets a semantic element, choose a visual-tree snapshot whose `screenshotFrameId` matches that frame or whose timestamp clearly corresponds to it. Preserve the snapshot ID, automation ID, element metadata, and bounds returned by that tree.

Geometry coordinates are normalized to the captured frame:

- `x = left / frameWidth`
- `y = top / frameHeight`
- `width = regionWidth / frameWidth`
- `height = regionHeight / frameHeight`

Clamp values to `0.0` through `1.0` and verify the region stays inside the frame. Do not estimate geometry without inspecting the exported frame or trustworthy visual-tree bounds.

## Write A UI Annotation From A CLI JSON File

Create a complete annotation JSON file and pass it to the CLI:

```json
{
  "annotationId": "checkout-submit-disabled",
  "startUtc": "2026-08-28T03:14:15Z",
  "label": "Submit button remained disabled",
  "source": "agent",
  "notes": "Observed after the address request completed.",
  "geometry": [
    {
      "geometryId": "checkout-submit-disabled-rect",
      "frameId": "<frame-id>",
      "capturedAtUtc": "2026-08-28T03:14:15Z",
      "kind": 1,
      "x": 0.62,
      "y": 0.71,
      "width": 0.25,
      "height": 0.08,
      "text": "Disabled submit control",
      "strokeColor": "#FF3B30",
      "strokeWidth": 2
    }
  ],
  "target": {
    "kind": "visualTreeElement",
    "source": "agent",
    "targetId": "<target-id>",
    "visualTreeSnapshotId": "<snapshot-id>",
    "type": "Button",
    "elementKind": "button",
    "label": "Submit",
    "automationId": "CheckoutSubmit",
    "normalizedBounds": {
      "x": 0.62,
      "y": 0.71,
      "width": 0.25,
      "height": 0.08
    }
  }
}
```

Then upsert and verify:

```sh
ansight session annotation upsert <session-id> --file <annotation.json> --json
ansight session annotations <session-id> --json
```

The CLI file contract uses `geometry`, singular. Its geometry enum is numeric: point `0`, rectangle `1`, ellipse `2`, free draw `3`, line `4`, and arrow `5`. Each geometry requires a unique `geometryId`, exact `frameId`, `capturedAtUtc`, `kind`, `x`, and `y`; rectangles and ellipses also need width and height, while free-draw, line, and arrow shapes need normalized points appropriate to the shape.

A target is optional. Include it only when it was derived from an actual visual-tree snapshot. Geometry identifies what region to draw, while the target records semantic element context.

## Use Diagnostic Annotation Tools Without Mixing Schemas

When the client exposes `ansight_inject_annotation`, use that tool directly for creation. It accepts `geometries`, plural, and string kinds `point`, `rectangle`, `ellipse`, or `freeDraw`. It can resolve by `sessionId` or an unambiguous `appId` and can default `startUtc` to the session's latest timestamp.

Use `ansight_update_annotation` for a partial update when available. It supports source guards and explicit clear operations, avoiding accidental loss of fields. Use `ansight_delete_annotation` only for an authorized removal.

Do not copy a diagnostic-tool `geometries` payload into the CLI `--file` contract, and do not send CLI numeric kinds to the diagnostic tools.

## Preserve Existing Annotations During CLI Updates

Before updating an existing ID through the CLI:

1. Read the annotation with `session annotations`.
2. Copy the full existing object into a working JSON file.
3. Change only the intended fields.
4. Upsert with `--file`.
5. Read it back and compare the ID, timestamps, geometry frame IDs, target snapshot ID, and source.

Do not update a UI annotation with only `--label` and `--start`; that replacement would omit its existing geometry and target.

## Verify Or Remove

Verification should confirm that the stored annotation belongs to the intended session and that its time and frame fall within the capture. Export the referenced screenshot again when spatial placement matters.

Delete through the CLI only when explicitly requested:

```sh
ansight session annotation delete <session-id> <annotation-id> --json
```

Report the session ID, annotation ID, source, time range, frame and snapshot IDs, geometry kind and normalized bounds, verification performed, and whether an existing annotation was replaced or removed.

## Present The Replay

Present the same session as the review artifact, reading back any annotations added or changed first. Annotations are optional emphasis, not a prerequisite for presenting proof:

- Prefer an available Ansight inline replay tool. If `show_replay` is exposed, use its advertised schema with the exact `sessionId`. Supply a verified `annotationId` only when it highlights a moment or region directly relevant to the work; otherwise omit it. Discover the installed tool; do not assume it is available or print a tool call as a substitute for invoking it.
- Keep recorded touch overlays visible when supported. A timeline scrubber is sufficient. Missing touches are a capture gap, not permission to invent an overlay from intended actions.
- Include an actual link to the full detail replay even when presenting an inline widget. For a local host, obtain `explorerUrl` from `ansight host status --json` and set its `sessionId` query parameter to the URL-encoded exact session ID, preserving other query parameters. Do not guess a port or invent an annotation deep-link parameter. If the explorer is unavailable, `ansight session serve <session-id> --json` can return a local `replayUrl`; report a failure to obtain one instead of fabricating a link. Local links require access to the machine serving them.
- Finish with what was verified and any remaining gaps. Mention relevant marker labels and timestamps when they help the user locate a specific area of interest. If inline tools are unavailable or fail, use the full replay link and state that it opens separately. An exported screenshot can supplement the replay when useful, but is not an interactive replay.
