---
name: ansight-install-cordova
description: Install @ansight/capacitor with automatic simulator or emulator registration and one-shot CLI QR enrollment for physical devices. Add and sync the Capacitor bridge, initialize native defaults, expose enrollFromQrCode from a developer-only surface, keep remote tools development-only, verify web and native builds, and inspect the app through the Ansight CLI.
---

# Ansight Cordova / Capacitor Install Skill

Use this skill for Capacitor 8 apps. Classic Apache Cordova is not currently a
drop-in target for `@ansight/capacitor`.

## Capture Choice

This skill implements an explicitly selected SDK integration. If the user only wants capture or inspection, explain SDK capture and external physical/virtual device capture before changing the app; preserve any choice already made. External capture needs no SDK, rebuild, or pairing QR. Follow `https://www.ansight.ai/skills/agents/ansight-operate-live-app/SKILL.md` for that route. Continue below when SDK installation is requested or selected.

## Workflow

1. Inspect `package.json`, Capacitor config, web bootstrap, native projects,
   debug guard, and existing developer menu.
2. Install the latest release and sync:

```shell
npm install @ansight/capacitor@latest
npx cap sync
```

3. Initialize from development-only bootstrap:

```ts
if (isDevelopmentBuild) {
  await Ansight.initializeAndActivate(
    Ansight.createOptionsBuilder()
      .withAnsightDefaults()
      .withReadOnlyToolAccess()
      .build(),
  );
}
```

   Replace `isDevelopmentBuild` with the app's existing explicit development
   variant flag; do not hard-code it to `true`.

4. Add a developer-only scanner action:

```ts
await Ansight.enrollFromQrCode({
  clientName: "My Capacitor App",
});
```

5. `scanPairingQrCode(...)` is an API alias. Use `connect(payload, ...)` when
   the app already owns a scanner.
6. Keep DOM and native tools behind the app's local-development guard. Prefer
   read-only DOM and native inspection; leave actions, reflection, secure
   storage, writes, and deletes off unless required.
7. Android requires no app camera permission for the SDK scanner. On iOS, add
   `NSCameraUsageDescription` and `NSLocalNetworkUsageDescription`. Add no ATS
   exception or unrelated permissions.
8. Create or update `ansight-readme.md` with the scanner location, selected
   tools, build checks, and CLI enrollment commands. Do not include an enrollment payload.
9. Run package checks, `npx cap sync`, and practical native debug builds.
10. Start or reuse the resident CLI host. A simulator or emulator registers
    automatically. For a physical device, issue one generic host QR and scan it
    from the developer-only surface:

```sh
ansight host run
ansight pairing issue --qr
ansight session list --connected --app-id <app-id> --json
ansight app tools <session-id> --detail summary --include-unavailable --max-results 50 --json
```

    Do not require `ansight app register` before the first connection. After
    the SDK supplies its real App ID, optionally link the discovered app to the
    repository with `ansight app register <app-id> --codebase <repository-path>`.

Simulator and emulator builds register automatically through native loopback.
The first physical-device scan registers the native app installation. Its random installation
id and enrollment state remain in app-private storage, so later developer
launches reconnect automatically.

## Recommended Inspection Skills

Use the main router to select the core inspection workflow. Add the Capacitor
companion only when DOM, bridge, or native-platform semantics are material:

```text
https://www.ansight.ai/skills/agents/ansight-app-inspection.md
https://www.ansight.ai/skills/cordova/ansight-app-inspection-cordova.md
```
