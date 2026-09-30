---
name: ansight-app-inspection
description: Route an ambiguous Ansight app request to the narrowest live-operation, remote-tool, session-investigation, annotation, readiness-audit, workspace-automation, or platform-inspection skill. Use only when the request does not already identify the required Ansight workflow.
---

# Ansight App Inspection Router Skill

Use this skill to choose a workflow, not to perform one. Once a specialist is selected, load it completely, stop following this router, and do not route back here from that specialist.

## Goal

Select one primary skill that owns the requested outcome. Add another skill only when the request crosses a real capability boundary.

## Respect Capture Choice

Ansight supports SDK capture, external simulator/emulator capture, and external physical-phone capture. Reuse the user's chosen mode or a matching session that satisfies the request. When the mode is unspecified and the choice affects app changes, setup, or evidence, the live-operation skill explains the options before proceeding. An SDK is required for app-internal tools and recorded user touches; external capture can provide visible UI operation and retained evidence without changing the app. See [Capture modes](https://www.ansight.ai/docs/capture-modes).

## Choose The Primary Skill

| Request | Use this skill |
| --- | --- |
| Choose capture mode, monitor an installed app without an SDK, boot a target, launch or stop an app, resolve a live session, interact, and verify | Installed `$ansight-operate-live-app`; fallback: `https://www.ansight.ai/skills/agents/ansight-operate-live-app/SKILL.md` |
| Discover and call bundled or app-specific tools exposed by a connected app | Installed `$ansight-use-remote-app-tools`; fallback: `https://www.ansight.ai/skills/agents/ansight-use-remote-app-tools/SKILL.md` |
| Inspect a live or recorded session, correlate telemetry and other evidence, or create a non-destructive session slice | Installed `$ansight-investigate-session`; fallback: `https://www.ansight.ai/skills/agents/ansight-investigate-session/SKILL.md` |
| Add or update a timeline annotation, screenshot geometry, or visual-tree target | Installed `$ansight-annotate-session`; fallback: `https://www.ansight.ai/skills/agents/ansight-annotate-session/SKILL.md` |
| Score integration strength or automation readiness and produce a remediation plan | Installed `$ansight-assess-automation-readiness`; fallback: `https://www.ansight.ai/skills/agents/ansight-assess-automation-readiness/SKILL.md` |
| Create or maintain tests, tasks, triggers, Trends definitions, sanitizers, schemas, or workspace registration | Installed `$ansight-ui-testing`; fallback: `https://www.ansight.ai/skills/agents/ansight-ui-testing/SKILL.md` |
| Prepare the CLI, resident host, or workstation dependencies | Installed `$ansight-cli-setup`; fallback: `https://www.ansight.ai/skills/ansight-cli-setup.md` |

This table selects ownership, not a sequence. The chosen specialist owns its prerequisite checks, authorization boundaries, commands, verification, and reporting.

## Combine Skills Sparingly

- Pair live operation with remote tools only when the request needs both visible UI behavior and in-process state or operations.
- Pair a core evidence skill with one platform inspection skill only when framework internals, platform logging, reflection suites, or platform-specific tool semantics matter.
- Pair investigation with annotation only when the user asks both to analyze evidence and to write review metadata. Reading an annotation does not require the annotation skill.
- A readiness audit may use live operation, remote tools, investigation, or a platform skill as evidence helpers, but the readiness skill remains the owner of scoring and reporting. Load those helpers directly; never reload this router.
- Workspace authoring does not require live-operation or investigation skills unless the user also requests execution or runtime verification.

## Platform Inspection Skills

| App shape | Use this skill |
| --- | --- |
| .NET MAUI, .NET for Android, .NET for iOS, .NET Mac Catalyst | `https://www.ansight.ai/skills/dotnet/ansight-app-inspection-dotnet.md` |
| Native iOS SwiftUI or UIKit | `https://www.ansight.ai/skills/ios/ansight-app-inspection-ios.md` |
| Native Android Kotlin or Java | `https://www.ansight.ai/skills/android/ansight-app-inspection-android.md` |
| React Native or Expo development build with iOS and Android native projects | `https://www.ansight.ai/skills/react-native/ansight-app-inspection-react-native.md` |
| Flutter with Android and/or iOS targets | `https://www.ansight.ai/skills/flutter/ansight-app-inspection-flutter.md` |
| Cordova-family or Ionic app built with Capacitor 8 | `https://www.ansight.ai/skills/cordova/ansight-app-inspection-cordova.md` |

If more than one app or platform is present, ask which running app/session should be inspected unless the user supplied an explicit app id, session id, device, or app name.

## Detection Hints

Inspect the repo and `ansight app list --json` before choosing. For an SDK session, also use focused `ansight app tools
<session-id> --query <terms> --detail summary --json` results when needed.
Do not load an unfiltered full tool catalog merely to identify a platform:

- `.csproj`, `MauiProgram.cs`, `TargetFramework` values such as `net*-android`, `net*-ios`, or `net*-maccatalyst`: use the .NET inspection skill.
- `.xcodeproj`, `.xcworkspace`, `Package.swift`, `Podfile`, `Info.plist`, SwiftUI `App`, or UIKit `AppDelegate` without React Native app files: use the native iOS inspection skill.
- `settings.gradle`, `build.gradle`, `build.gradle.kts`, `AndroidManifest.xml`, and Kotlin or Java `Application` without React Native app files: use the native Android inspection skill.
- `package.json` with `react-native` or `expo`, generated `ios/` and `android/` projects, `metro.config.*`, or `@react-native/*`: use the React Native inspection skill.
- `pubspec.yaml` with Flutter, `lib/main.dart`, and Flutter target folders: use the Flutter inspection skill.
- `package.json` with `@capacitor/core`, `capacitor.config.*`, and native target folders: use the Cordova / Capacitor inspection skill.

React Native and Expo, Flutter, and Capacitor take precedence over their generated native target folders. Expo Go cannot load the native SDK bridge; SDK integration requires an Expo development build. That restriction does not prohibit external capture of an installed app. A .NET MAUI app takes precedence over its `Platforms/iOS` and `Platforms/Android` folders.

External accessibility trees identify rendered platform UI, not the app's implementation framework. When source clues or authoritative framework metadata are unavailable, use generic evidence skills rather than inferring SwiftUI, Compose, MAUI, Flutter, React, or DOM internals. For SDK sessions, the remote app tool catalog can provide framework clues:

- MAUI or `.NET` names, `maui.*` tools, or .NET package metadata: .NET.
- React tool ids such as `react.get_component_tree` or `react.get_shadow_tree`: React Native.
- Flutter tool ids such as `flutter.get_widget_tree` or `flutter.find_widgets`: Flutter.
- DOM tool ids such as `dom.get_document` with adapter metadata `@ansight/capacitor`: Cordova / Capacitor.
- Native iOS bundle ids, UIKit visual-tree metadata, or Swift package names: native iOS.
- Android package ids, Android view hierarchy metadata, or Gradle module names: native Android.
- `reflect.*` tool ids indicate a platform reflection suite is installed; route by app/session metadata and neighboring platform tool families before using reflection.

## Route And Stop

1. Classify the requested outcome and select one primary skill from the table.
2. Inspect repository markers only when a platform skill may be required.
3. Add at most the directly needed platform or evidence helper skills.
4. Report missing prerequisites. Route to CLI setup or SDK installation only when setup or installation is already within the requested scope.
5. Stop following this router and continue with the selected skill or deliberate combination.

If platform detection remains ambiguous, generic evidence skills can still work with standard Ansight surfaces. Ask the user to choose a platform only before a platform-specific operation would materially change the workflow.
