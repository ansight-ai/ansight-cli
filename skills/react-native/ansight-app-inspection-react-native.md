---
name: ansight-app-inspection-react-native
description: Add React Native and Expo development-build semantics to an Ansight operation. Use alongside a selected core Ansight workflow when React component, shadow-tree, native hierarchy, navigation, memory, or React tool behavior affects interpretation; do not use as the primary live-operation, remote-tool, session-investigation, or annotation workflow.
---

# Ansight React Native Inspection Semantics

Use this companion skill for React Native evidence. React app-internal tools require native Ansight integration in an Expo development build; Expo Go cannot load that bridge. External capture does not require it. Do not use this skill for native-only iOS or Android, .NET, Flutter, or web-only projects.

## Keep The Core Workflow In Charge

Select the skill that owns the requested outcome before applying these React Native semantics:

- live lifecycle or visible UI interaction: `https://www.ansight.ai/skills/agents/ansight-operate-live-app/SKILL.md`
- bundled or app-specific remote tools: `https://www.ansight.ai/skills/agents/ansight-use-remote-app-tools/SKILL.md`
- live or recorded evidence investigation: `https://www.ansight.ai/skills/agents/ansight-investigate-session/SKILL.md`
- annotation writes: `https://www.ansight.ai/skills/agents/ansight-annotate-session/SKILL.md`

The selected core skill owns session selection, authorization, command procedure, verification, reporting, and any relevant workspace reuse. Use this skill only to interpret React Native-specific evidence or choose among cataloged React and native tool families.

## Capture Source Semantics

SDK and external capture are both valid for visible UI and retained evidence on supported iOS/Android targets. Keep the core skill's chosen mode. External capture exposes device screenshots and accessibility UI; React component/shadow trees, props, state, navigation references, and JavaScript internals require an SDK session and the relevant advertised tools. Do not infer framework internals from an external accessibility tree or install an SDK merely because those internals are unavailable. Native logs, metrics, and sandbox files follow the physical/virtual platform limits in the live-operation skill.

## Choose The Correct Tree

- Use a cataloged React component tree for composite-component ownership, logical structure, and explicitly available props or state.
- Use a cataloged React shadow tree for the committed host layout with composite components flattened out.
- Use cataloged native `ui.*` tools for UIKit or Android hierarchy, native screenshots, platform widgets, overlays, and native rendering or hit-testing issues.
- Treat React component IDs, shadow nodes, native nodes, automation IDs, and reflection roots as separate namespaces unless a returned schema explicitly connects them.
- Compare React and native evidence when diagnosing a mismatch between JavaScript state and rendered platform UI.

## Interpret React Native Tools

- Treat `react.*`, native UI, storage, artifact, custom, and `reflect.*` tools as optional catalog entries rather than guaranteed React Native capabilities.
- Use `react.get_component_tree`, `react.get_shadow_tree`, `react.find_components`, `react.get_component`, or `react.get_navigation_state` only when the current catalog returns them.
- Navigation state requires an app-registered navigation reference. Props and state may be absent or sanitized; do not treat omission as a runtime failure.
- Use React Native memory channels from retained telemetry when available before assuming JavaScript heap data requires a separate sampler.
- `react.invoke_component_action` and other React actions are remote writes. Require the action to be cataloged and app-allow-listed, then follow the core remote-tool skill's authorization and verification rules.
- Native `reflect.*` tools operate only on registered native roots and should remain development-only, such as under `__DEV__` or a native Debug build.

## Platform Fallbacks

Use Metro output, native build output, Xcode, ADB, simulator or emulator tools, app-container access, or source inspection only after the owning core skill identifies a concrete Ansight capability gap. Report whether the gap was in the React tree, native hierarchy, JavaScript or native tool catalog, retained evidence, or device control.

## Report React Native-Specific Evidence

In addition to the core skill's report, name the React Native App ID and native bundle or application ID when available, identify every tree type used, and distinguish React inference from observed native evidence.
