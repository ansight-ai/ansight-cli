---
name: ansight-install-ios
description: Install the native iOS Ansight SDK with automatic Simulator registration and one-shot CLI QR enrollment for physical devices. Add the aggregate SwiftPM product or pod, initialize from app startup, expose the SDK scanner from a developer-only surface, add only Apple-required privacy descriptions, verify the build, and inspect the app through the Ansight CLI.
---

# Ansight iOS Install Skill

Use this skill for native SwiftUI or UIKit apps.

## Capture Choice

This skill implements an explicitly selected SDK integration. If the user only wants capture or inspection, explain SDK capture and external physical/virtual device capture before changing the app; preserve any choice already made. External capture needs no SDK, rebuild, or pairing QR. Follow `https://www.ansight.ai/skills/agents/ansight-operate-live-app/SKILL.md` for that route. Continue below when SDK installation is requested or selected.

## Outcome

The finished app must:

- link the aggregate `Ansight` product for local development;
- initialize `AnsightRuntime.shared` once from app startup;
- register automatically in Simulator or Mac Catalyst and expose the SDK QR
  scanner from a developer-only UI for physical devices;
- reconnect automatically after the first successful scan;
- add only the privacy descriptions required by the chosen flow; and
- require no generated JSON, build setting secret, certificate, or manual app
  registration.

## Workflow

1. Identify the app target, bundle id, startup delegate, dependency manager,
   debug configuration, and existing developer menu.
2. Add the aggregate SwiftPM product:

```swift
.package(
    url: "https://github.com/ansight-ai/ansight-sdk.git",
    exact: "1.4.0-preview.1"
)
```

   Use `pod 'Ansight', '1.4.0-preview.1'` when the project already uses
   CocoaPods.
3. Keep both the import and initialization behind the app's Debug or explicit
   internal-build compilation condition. Initialize once:

```swift
#if DEBUG
try AnsightRuntime.shared.initializeAndActivateAnsightSdk()
#endif
```

4. Add a developer-only scanner action:

```swift
let result = await AnsightRuntime.shared.connect(
    .qrCode(title: "Scan Ansight Enrollment QR")
)
```

5. Add `NSCameraUsageDescription` because the SDK scanner uses the camera. Add
   `NSLocalNetworkUsageDescription` for physical devices connecting to the CLI host.
   Do not add Bluetooth, location, contacts, photos, Bonjour, or ATS clear-text
   exceptions.
6. Keep reflection, secure-storage access, writes, and deletes disabled unless
   the requested development workflow requires them.
7. Create or update `ansight-readme.md` with the scanner location, build
   verification command, and CLI enrollment commands. Do not add an enrollment
   payload or token to it.
8. Build the relevant simulator and, where practical, physical-device target.
9. Start or reuse the resident CLI host. Simulator and Mac Catalyst builds
   register automatically. For a physical device, issue one generic host QR and
   scan it from the developer-only surface:

```sh
ansight host run
ansight pairing issue --qr
ansight session list --connected --app-id <app-id> --json
ansight app tools <session-id> --detail summary --include-unavailable --max-results 50 --json
```

   Do not require `ansight app register` before the first connection. After the
   SDK supplies its real App ID, optionally link the discovered app to the
   repository with `ansight app register <app-id> --codebase <repository-path>`.

## Enrollment Behavior

Simulator and Mac Catalyst register automatically through loopback. The first
physical-device scan registers the app installation and stores a random installation
id and enrollment state privately. Later developer launches reconnect
automatically. The client uses Network.framework for its local clear-text
`ws://` development connection, so no ATS exception or certificate is needed.

## Recommended Inspection Skills

Use the main router to select the core inspection workflow. Add the native iOS
companion only when SwiftUI, UIKit, or Apple-platform semantics are material:

```text
https://www.ansight.ai/skills/agents/ansight-app-inspection.md
https://www.ansight.ai/skills/ios/ansight-app-inspection-ios.md
```
