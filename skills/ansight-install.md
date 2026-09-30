---
name: ansight-install
description: Configure Ansight when the capture mode or SDK platform is not yet selected. Explain SDK and external device capture choices, verify CLI dependencies, route external monitoring to live operation or SDK integration to the matching platform install skill, and verify the selected session.
---

# Ansight Install Router Skill

Use this skill when the user asks an agent to install or configure Ansight and the prompt does not already name the capture mode or target SDK.

This is a routing skill. Select the capture mode first; for SDK integration, identify the app platform and load the matching platform install skill as the source of truth.

## Choose Capture Before App Changes

Preserve an explicit SDK or external choice. If the user only asks to configure Ansight, explain the relevant options and ask which they want before changing app code:

- **External simulator/emulator:** no SDK or rebuild; installed app, booted virtual target, host, and platform tools. Captures screens, accessibility UI, native logs, available metrics, and accessible sandbox files.
- **External physical phone:** no app changes; exact device ID. iPhone needs Mac/Xcode, a trusted unlocked phone with Developer Mode and UI Automation, Appium/XCUITest, and signed WebDriverAgent. Android needs authorized ADB and USB debugging. Capture ends when the app leaves the foreground; evidence differs by platform.
- **SDK on physical or virtual devices:** richest app evidence, including recorded touches and enabled network capture, framework state, custom events, and app tools. Requires an SDK-enabled trusted build; virtual targets register automatically and phones pair with a one-time QR.

External automation can send input but cannot record the user's touches or inspect app internals. SDK session capture itself requires neither Appium, iOS Developer Mode, nor Android USB debugging; deployment, device control, and profiling have separate requirements. See [Capture modes](https://www.ansight.ai/docs/capture-modes).

For external capture, complete CLI/dependency setup as needed, then follow `https://www.ansight.ai/skills/agents/ansight-operate-live-app/SKILL.md` for monitoring and session verification. Do not add SDK packages, change the app, or issue a pairing QR for that route. The remaining platform install rules apply to SDK capture.

## CLI Prerequisite

Treat the official website installers as the only supported CLI bootstrap. Do
not reconstruct their download, checksum, PATH, skill, account, or resident-host
steps manually.

1. Detect the current operating system and whether `ansight` resolves on PATH.
2. If the CLI is missing, show the matching command and ask the user to approve
   running it. If the agent is authorized to install developer tooling, run the
   approved command directly; otherwise ask the user to run it and wait for the
   result.

   macOS or Linux:

   ```sh
   curl -fsSL https://www.ansight.ai/install.sh | bash
   ```

   Windows PowerShell:

   ```powershell
   irm https://www.ansight.ai/install.ps1 | iex
   ```

3. Open a new shell if PATH changes are not visible, then verify the installed
   CLI rather than assuming installation succeeded:

   ```sh
   ansight version --json
   ansight doctor --json
   ```

The scripts are published with the website at
`https://www.ansight.ai/install.sh` and
`https://www.ansight.ai/install.ps1`.

## Doctor-Led Dependency Setup

Use `ansight doctor --json` as the source of truth for the current machine.
Do not guess dependencies from the app platform alone.

- Explain every required failure and every optional capability that affects the
  requested workflow.
- Before using Homebrew, apt, dnf, winget, dotnet tool installation, npm, or any
  other package manager, show the exact packages and commands and ask the user
  to approve installing them.
- Do not install an optional dependency merely because Doctor reports it as
  missing. Ask whether the user wants the feature it enables.
- After approved dependency changes, rerun `ansight doctor --json` and report
  what changed. Do not claim the workstation is ready while a required check is
  still failing.
- Use `https://www.ansight.ai/docs/cli/dependencies` for the platform-specific
  installation commands and the feature enabled by each dependency.

## Routing Rules

Inspect the repo before choosing:

- Use the .NET install skill for .NET MAUI, .NET for Android, .NET for iOS, and .NET Mac Catalyst apps.
- Use the iOS install skill for native SwiftUI or UIKit apps.
- Use the Android install skill for native Android Kotlin apps.
- Use the React Native install skill for React Native apps and Expo development builds with generated iOS and Android native projects.
- Use the Flutter install skill for Flutter apps with Android and/or iOS targets.
- Use the Cordova / Capacitor install skill for Cordova-family or Ionic apps built with Capacitor 8.

If more than one app is present, ask which app should be configured unless the user's prompt clearly identifies one.

## Remote Tool Default

When the selected install enables reflection, mention the concrete `reflect.*` tool family and default the reflection package or suite to a development-time dependency: Debug or explicit local-development variants only, with runtime guards. Do not enable `reflect.*` for distributable builds unless the user explicitly requests it and accepts the policy.

## Platform Skills

| Platform | Use This Skill |
| --- | --- |
| .NET MAUI, .NET Android, .NET iOS, .NET Mac Catalyst | `https://www.ansight.ai/skills/dotnet/ansight-install-dotnet.md` |
| Native iOS SwiftUI or UIKit | `https://www.ansight.ai/skills/ios/ansight-install-ios.md` |
| Native Android Kotlin | `https://www.ansight.ai/skills/android/ansight-install-android.md` |
| React Native / Expo development build | `https://www.ansight.ai/skills/react-native/ansight-install-react-native.md` |
| Flutter | `https://www.ansight.ai/skills/flutter/ansight-install-flutter.md` |
| Cordova / Capacitor | `https://www.ansight.ai/skills/cordova/ansight-install-cordova.md` |

## Detection Hints

- `.csproj`, `MauiProgram.cs`, `TargetFramework` values such as `net*-android`, `net*-ios`, or `net*-maccatalyst`: .NET.
- `.xcodeproj`, `.xcworkspace`, `Package.swift`, `Podfile`, `Info.plist`, SwiftUI `App`, or UIKit `AppDelegate` without React Native app files: native iOS.
- `settings.gradle`, `build.gradle`, `build.gradle.kts`, `AndroidManifest.xml`, and Kotlin `Application` without React Native app files: native Android.
- `package.json` with `react-native` or `expo`, generated `ios/` and `android/` projects, `metro.config.*`, or `@react-native/*`: React Native / Expo development build.
- `pubspec.yaml` with a Flutter SDK dependency, `lib/main.dart`, and Flutter `android/` or `ios/` targets: Flutter.
- `package.json` with `@capacitor/core`, `capacitor.config.*`, and native `android/` or `ios/` projects: Cordova / Capacitor.

React Native and Expo, Flutter, and Capacitor take precedence over the native iOS or Android project folders they contain. Expo Go cannot load the native Ansight bridge; route only Expo development builds with generated native projects. If a classic Apache Cordova app does not use Capacitor 8, report that the current `@ansight/capacitor` adapter is not a drop-in target.

## Workflow

1. Install and verify the CLI through the [CLI prerequisite](#cli-prerequisite).
2. Run the [Doctor-led dependency setup](#doctor-led-dependency-setup), obtaining approval before installing missing tools.
3. Resolve the capture choice. For external capture, follow the live-operation skill and verify its session; stop this SDK-install workflow here.
4. For SDK capture, inspect the repository root and likely app directories.
5. Identify the target platform and app module.
6. If the current agent supports subagents or thread forking, fork the implementation into the matching platform-specific skill; otherwise load it in the current task.
7. Continue with the selected skill's workflow, including automatic host-local registration, one-shot physical-device QR enrollment, runtime guards, minimal platform privacy declarations, verification, and `ansight-readme.md`.
8. Use the Ansight CLI for host status, generic physical-device QR enrollment, connected-session discovery, optional post-discovery app-to-codebase registration, and tool verification. If CLI setup needs more detail, load `https://www.ansight.ai/skills/ansight-cli-setup.md`.
9. Include the selected capture mode and skill URL in the final response so the user can audit which route was used.

Do not claim a platform-specific setup was completed until the selected skill's verification steps have actually passed or the remaining manual steps are listed.
