---
name: ansight-app-inspection-android
description: Add native Android Kotlin and Java semantics to an Ansight operation. Use alongside a selected core Ansight workflow when Android view or Compose rendering, preferences, storage, reflection, ADB fallback, or Android tool behavior affects interpretation; do not use as the primary live-operation, remote-tool, session-investigation, or annotation workflow.
---

# Ansight Android Inspection Semantics

Use this companion skill for native Android Kotlin or Java evidence. Do not use it for React Native, .NET for Android, Flutter, native iOS, or generic JVM work.

## Keep The Core Workflow In Charge

Select the skill that owns the requested outcome before applying these Android semantics:

- live lifecycle or visible UI interaction: `https://www.ansight.ai/skills/agents/ansight-operate-live-app/SKILL.md`
- bundled or app-specific remote tools: `https://www.ansight.ai/skills/agents/ansight-use-remote-app-tools/SKILL.md`
- live or recorded evidence investigation: `https://www.ansight.ai/skills/agents/ansight-investigate-session/SKILL.md`
- annotation writes: `https://www.ansight.ai/skills/agents/ansight-annotate-session/SKILL.md`

The selected core skill owns session selection, authorization, command procedure, verification, reporting, and any relevant workspace reuse. Use this skill only to interpret Android-specific evidence or choose among cataloged Android tool families.

## Capture Source Semantics

SDK and external capture are both valid for visible UI and retained evidence on supported iOS/Android targets. Keep the core skill's chosen mode. External capture exposes device screenshots and accessibility UI; in-process Android views, Compose state, reflection, and app-owned storage require an SDK session and the relevant advertised tools. Do not infer framework internals from an external accessibility tree or install an SDK merely because those internals are unavailable. Native logs, metrics, and sandbox files follow the physical/virtual platform limits in the live-operation skill.

## Interpret Android UI Evidence

- Use cataloged native `ui.*` tools when the Android hierarchy, screenshots, node details, overlays, platform widgets, or native hit testing are material.
- Treat native visual-tree output as the rendered Android hierarchy. For Jetpack Compose or mixed UIs, do not infer the complete composable source structure or state from host-view evidence alone.
- Correlate screenshots with the corresponding tree when layout, labels, visibility, hit targets, or navigation state matter.

## Interpret Android State And Tools

- Treat native UI, database, file, SharedPreferences, secure-storage, artifact, and `reflect.*` tools as optional catalog entries rather than guaranteed Android capabilities.
- Use SharedPreferences and secure-storage tools only for stores, keys, or prefixes explicitly exposed by the catalog and app guard.
- Use `reflect.*` only against returned registered roots. It normally comes from a Debug or explicit local-development dependency such as `ai.ansight:ansight-tools-reflection-android`.
- An absent tool family may reflect build-variant gating, pairing policy, or local guards; report the observed catalog rather than changing the integration automatically.

## Emulator And Device Semantics

- Prefer Ansight lifecycle, semantic UI, and evidence surfaces before direct ADB access.
- Use `adb`, emulator or device logs, Gradle output, app-data extraction, or source inspection only after the owning core skill identifies a concrete Ansight capability gap.
- State whether a fallback observed the Android process, operating-system logs, package data, build output, or UI outside Ansight.

## Report Android-Specific Evidence

In addition to the core skill's report, name the Android application ID and device or emulator when available, distinguish native hierarchy evidence from Compose inference, and identify any storage, reflection, or ADB fallback surface used.
