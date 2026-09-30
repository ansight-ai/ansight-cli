# External Capture

Use this reference after choosing external capture. It needs an installed app and platform access; no SDK, pairing QR, or app rebuild is required. Check the installed CLI's help when an option is unavailable.

## Resolve The Target And Watch

```sh
ansight host status --json
ansight doctor --json
ansight device list --all --json
ansight app watch list --json
```

Reuse a watch only when its App ID, platform, exact device selection, and settings match. Watches do not boot devices or install apps. Physical phones require `--platform` and an exact `--device-id`; never silently choose a phone from discovery. Start a virtual target only when needed for the requested work.

For one selected simulator or emulator:

```sh
ansight app watch add <app-id> --platform <ios|android> --device-id <virtual-device-id> --json
```

Omit the device restriction only when monitoring all matching virtual devices is intended:

```sh
ansight app watch add <app-id> --json
```

That discovers booted iOS simulators and Android emulators, including targets booted later. Each device gets a separate session. It does not automatically select physical phones.

For a physical Android phone, enable USB debugging, authorize the host in ADB, and select its exact serial:

```sh
ansight app watch add <app-id> --platform android --device-id <adb-serial> --json
```

For a physical iPhone, use macOS and Xcode, pair and trust an unlocked phone, enable Developer Mode and UI Automation, and prepare Appium/XCUITest with a signed WebDriverAgent. Confirm prerequisites rather than installing or changing signing settings automatically. See [Physical iPhone setup](https://www.ansight.ai/docs/local-player/external-monitoring#set-up-monitoring-from-the-cli).

```sh
ansight app watch add <app-id> --platform ios --device-id <coredevice-id> --json
```

Add `--instruments` only when native profiling is wanted. It is an optional physical-iPhone capture whose CPU/memory samples arrive after recording/export; a trace may remain available even when sample export fails. Appium is unnecessary for Android or iOS simulator external capture.

Launch or foreground the intended app, then resolve the watch's active session:

```sh
ansight device launch <ios|android> <device-id> <app-id> --json
ansight app watch list --json
ansight session list --connected --app-id <app-id> --limit 20 --json
ansight session show <session-id> --json
```

Cross-check the selected watch/device/session, `captureSource: device`, and `customProperties.deviceExecution.capabilities`. Use available providers and reasons to determine which UI, logs, metrics, and files can answer the request. An evidence-read capability permits reading retained data; it does not prove that a stream contains samples.

## Run Visible Automation

Use the live-operation skill's `app interact` or semantic `ui` workflow for direct interaction. For a bounded agent run, attach to the exact monitor session:

```sh
ansight app execute <live-session-id> --prompt "<goal>" --json
```

To launch an installed app without a live session, select device mode explicitly:

```sh
ansight app execute --app-id <app-id> --platform <ios|android> \
  --device-id <device-id> --execution-mode device --prompt "<goal>" --json
```

Do not combine a session ID with `--execution-mode` or other launch options. An attached execution reserves the session and releases it on completion; the monitor remains enabled. App Graphs and app-internal tools require an SDK provider. Tasks and triggers need schema version 2 with declared capability requirements for external execution; legacy version 1 retains its SDK requirement.

## Capture Settings And Cleanup

Screenshot interval defaults to 2000 ms, supports 100–60000 ms, and is a target frequency limited by sequential capture speed. Preserve the user's choice:

```sh
ansight app watch configure <watch-id> --screenshot-interval-ms <ms> --json
```

This preserves the monitor's other settings and updates active recordings. Re-adding a selection updates and enables it, so inspect before using `add` to change an existing monitor. `--capture-file <sandbox-relative-path>` on `add` is optional and repeatable; file access depends on platform permissions. It makes best-effort exit copies, not a transactional database snapshot. Physical iPhone watches cannot capture third-party sandbox files.

Virtual recordings follow process lifetime. Physical recordings end when the app leaves the foreground. An enabled watch may capture again later, including after a host restart. Disable only the requested monitor or one exclusively created for this work:

```sh
ansight app watch disable <watch-id> --json
ansight app watch list --json
```

Disabling ends every active capture belonging to that watch and retains its definition and recordings. Removing a watch also removes its definition while retaining recordings. If an existing watch spans several devices, terminating the selected app ends that run without disabling the other devices' monitoring. Report remaining watch state and verify the selected session ended.
