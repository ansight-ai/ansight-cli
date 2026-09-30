---
name: ansight-app-inspection-ios
description: Add native iOS SwiftUI and UIKit semantics to an Ansight operation. Use alongside a selected core Ansight workflow when UIKit hierarchy, SwiftUI hosting, Apple storage, Simulator control, reflection, or iOS tool behavior affects interpretation; do not use as the primary live-operation, remote-tool, session-investigation, or annotation workflow.
---

# Ansight iOS Inspection Semantics

Use this companion skill for native iOS SwiftUI or UIKit evidence. Do not use it for React Native, .NET for iOS, Flutter, native Android, or generic Swift package work.

## Keep The Core Workflow In Charge

Select the skill that owns the requested outcome before applying these iOS semantics:

- live lifecycle or visible UI interaction: `https://www.ansight.ai/skills/agents/ansight-operate-live-app/SKILL.md`
- bundled or app-specific remote tools: `https://www.ansight.ai/skills/agents/ansight-use-remote-app-tools/SKILL.md`
- live or recorded evidence investigation: `https://www.ansight.ai/skills/agents/ansight-investigate-session/SKILL.md`
- annotation writes: `https://www.ansight.ai/skills/agents/ansight-annotate-session/SKILL.md`

The selected core skill owns session selection, authorization, command procedure, verification, reporting, and any relevant workspace reuse. Use this skill only to interpret iOS-specific evidence or choose among cataloged iOS tool families.

## Capture Source Semantics

SDK and external capture are both valid for visible UI and retained evidence on supported iOS/Android targets. Keep the core skill's chosen mode. External capture exposes device screenshots and accessibility UI; UIKit in-process inspection, SwiftUI state, reflection, and app-owned storage require an SDK session and the relevant advertised tools. Do not infer framework internals from an external accessibility tree or install an SDK merely because those internals are unavailable. Native logs, metrics, and sandbox files follow the physical/virtual platform limits in the live-operation skill.

## Interpret iOS UI Evidence

- Use cataloged native `ui.*` tools when UIKit hierarchy, screenshots, node details, overlays, z-order, platform widgets, or native hit testing are material.
- SwiftUI content may appear through UIKit hosting containers. Treat the native visual tree as the rendered hierarchy, not the original SwiftUI source or complete SwiftUI state.
- Correlate screenshots with the corresponding tree when layout, labels, visibility, hit targets, or navigation state matter.

## Interpret iOS State And Tools

- Treat native UI, database, file, UserDefaults, Keychain, artifact, and `reflect.*` tools as optional catalog entries rather than guaranteed iOS capabilities.
- Use UserDefaults and Keychain tools only for stores, suites, services, accounts, and keys explicitly exposed by the catalog and app guard.
- Use `reflect.*` only against returned registered roots. It normally comes from a Debug or explicit local-development product such as `AnsightToolsReflection`.
- An absent tool family may reflect build gating, pairing policy, or local guards; report the observed catalog rather than changing the integration automatically.

## Simulator And Device Semantics

- For iOS Simulator touch and control, prefer Ansight's native SimulatorKit HID bindings. Do not introduce Appium for a simulator when that binding is available.
- Treat `xcrun simctl` as a lifecycle, discovery, screenshot, pasteboard, and simulator-state fallback rather than a touch-injection mechanism.
- A paired physical build can provide app-supplied logs and Ansight evidence. Use Xcode device logging only when complete OS or process console output not captured by the app is required.
- Reserve Appium or XCUITest for physical-device needs or an explicit, reported fallback.

## Platform Fallbacks

Use Xcode, `simctl`, app-container access, device logs, or source inspection only after the owning core skill identifies a concrete Ansight capability gap. Report the gap and the exact fallback used.

## Report iOS-Specific Evidence

In addition to the core skill's report, name the bundle identifier and device or simulator when available, distinguish SwiftUI inference from observed UIKit hierarchy, and identify any native, storage, reflection, or fallback surface used.
