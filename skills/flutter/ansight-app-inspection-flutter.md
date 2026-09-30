---
name: ansight-app-inspection-flutter
description: Add Flutter widget and native-platform semantics to an Ansight operation. Use alongside a selected core Ansight workflow when widget hierarchy, navigation, native rendering, physical-device logging, or Flutter tool behavior affects interpretation; do not use as the primary live-operation, remote-tool, session-investigation, or annotation workflow.
---

# Ansight Flutter Inspection Semantics

Use this companion skill for Flutter apps targeting iOS or Android. Do not route a native-only app here merely because generated Flutter platform folders are present elsewhere in the repository.

## Keep The Core Workflow In Charge

Select the skill that owns the requested outcome before applying these Flutter semantics:

- live lifecycle or visible UI interaction: `https://www.ansight.ai/skills/agents/ansight-operate-live-app/SKILL.md`
- bundled or app-specific remote tools: `https://www.ansight.ai/skills/agents/ansight-use-remote-app-tools/SKILL.md`
- live or recorded evidence investigation: `https://www.ansight.ai/skills/agents/ansight-investigate-session/SKILL.md`
- annotation writes: `https://www.ansight.ai/skills/agents/ansight-annotate-session/SKILL.md`

The selected core skill owns session selection, authorization, command procedure, verification, reporting, and any relevant workspace reuse. Use this skill only to interpret Flutter-specific evidence or choose among cataloged Flutter and native tool families.

## Capture Source Semantics

SDK and external capture are both valid for visible UI and retained evidence on supported iOS/Android targets. Keep the core skill's chosen mode. External capture exposes device screenshots and accessibility UI; Flutter widget trees, widget state, navigation internals, and app-owned storage require an SDK session and the relevant advertised tools. Do not infer framework internals from an external accessibility tree or install an SDK merely because those internals are unavailable. Native logs, metrics, and sandbox files follow the physical/virtual platform limits in the live-operation skill.

## Interpret Flutter Evidence

- Use cataloged `flutter.get_widget_tree`, `flutter.inspect_widget`, `flutter.find_widgets`, or `flutter.get_navigation_state` only when widget ownership, Flutter layout, widget state, or navigation semantics are material.
- Use cataloged native `ui.*` tools for UIKit or Android hierarchy, screenshots, platform views, overlays, and native rendering or hit-testing issues.
- Treat Flutter widget IDs, native nodes, automation IDs, and reflection roots as separate namespaces unless a returned schema explicitly connects them.
- Compare widget and native evidence when diagnosing a mismatch between Flutter state and rendered platform UI.
- Treat Flutter, native, storage, artifact, and `reflect.*` families as optional catalog entries, and treat any Flutter action tool as a remote mutation.

## Physical Device And Fallback Semantics

A paired physical build can stream app-provided logs, Ansight diagnostics, telemetry, screenshots, touches, and tool results. Use Xcode device logging or Android system logs only when complete OS or process output not captured by the app is required.

Use Flutter tooling, Xcode, ADB, simulator or emulator tools, device logs, or source inspection only after the owning core skill identifies a concrete Ansight capability gap. Report the gap and exact fallback.

## Report Flutter-Specific Evidence

In addition to the core skill's report, name the Flutter target platform, distinguish widget-tree evidence from native hierarchy evidence, and identify any Flutter tool or platform fallback used.
