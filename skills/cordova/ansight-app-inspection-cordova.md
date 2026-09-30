---
name: ansight-app-inspection-cordova
description: Add Capacitor DOM and native-platform semantics to an Ansight operation. Use alongside a selected core Ansight workflow when WebView DOM, JavaScript errors, native hierarchy, bridge state, or DOM tool behavior affects interpretation; do not use as the primary live-operation, remote-tool, session-investigation, or annotation workflow.
---

# Ansight Cordova / Capacitor Inspection Semantics

Use this companion skill for supported Cordova-family apps built with Capacitor. DOM and bridge tools require the Ansight Capacitor adapter; classic Cordova apps do not inherit that SDK support. External capture can inspect their installed native app UI without the adapter.

## Keep The Core Workflow In Charge

Select the skill that owns the requested outcome before applying these Capacitor semantics:

- live lifecycle or visible UI interaction: `https://www.ansight.ai/skills/agents/ansight-operate-live-app/SKILL.md`
- bundled or app-specific remote tools: `https://www.ansight.ai/skills/agents/ansight-use-remote-app-tools/SKILL.md`
- live or recorded evidence investigation: `https://www.ansight.ai/skills/agents/ansight-investigate-session/SKILL.md`
- annotation writes: `https://www.ansight.ai/skills/agents/ansight-annotate-session/SKILL.md`

The selected core skill owns session selection, authorization, command procedure, verification, reporting, and any relevant workspace reuse. Use this skill only to interpret Capacitor-specific evidence or choose among cataloged DOM and native tool families.

## Capture Source Semantics

SDK and external capture are both valid for visible UI and retained evidence on supported iOS/Android targets. Keep the core skill's chosen mode. External capture exposes device screenshots and accessibility UI; WebView DOM, JavaScript state, Capacitor bridge state, and app-owned storage require an SDK session and the relevant advertised tools. Do not infer framework internals from an external accessibility tree or install an SDK merely because those internals are unavailable. Native logs, metrics, and sandbox files follow the physical/virtual platform limits in the live-operation skill.

## Interpret DOM And Native Evidence

- Use cataloged `dom.get_document`, `dom.inspect_node`, or `dom.query_selector` when DOM ownership, WebView structure, attributes, styles, or bridge-rendered content are material.
- Use cataloged native `ui.*` tools for UIKit or Android hierarchy, screenshots, native overlays, platform views, and rendering or hit-testing outside the DOM.
- Treat DOM nodes, native nodes, automation IDs, and reflection roots as separate namespaces unless a returned schema explicitly connects them.
- Compare DOM and native evidence when diagnosing a mismatch between WebView state and rendered platform UI.
- `dom.invoke_action` and similar DOM actions are remote writes. Keep semantic device interaction as the default for a visible user flow and follow the core remote-tool skill for authorized internal operations.

## JavaScript, Bridge, And Fallback Semantics

A paired build may stream captured JavaScript errors, app-provided logs, Ansight diagnostics, telemetry, screenshots, touches, and tool results. Use browser devtools only when WebView console, network, or runtime evidence unavailable through Ansight is required; use Xcode or Android system logs for complete native process output.

Use browser devtools, Xcode, ADB, simulator or emulator tools, app-container access, or source inspection only after the owning core skill identifies a concrete Ansight capability gap. Report whether the gap was in the DOM adapter, native hierarchy, bridge, cataloged tools, retained evidence, or device control.

## Report Capacitor-Specific Evidence

In addition to the core skill's report, name the native target, distinguish DOM evidence from native hierarchy evidence, and identify any JavaScript, bridge, native, or fallback surface used.
