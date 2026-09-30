---
name: ansight-install-android
description: Install the native Android Ansight SDK with automatic emulator registration and one-shot CLI QR enrollment for physical devices. Add the aggregate dependency, initialize from Application, expose the SDK scanner from a developer-only surface, keep remote tools development-only, verify the Gradle build, and inspect the app through the Ansight CLI.
---

# Ansight Android Install Skill

Use this skill for native Android Kotlin or Java apps.

## Capture Choice

This skill implements an explicitly selected SDK integration. If the user only wants capture or inspection, explain SDK capture and external physical/virtual device capture before changing the app; preserve any choice already made. External capture needs no SDK, rebuild, or pairing QR. Follow `https://www.ansight.ai/skills/agents/ansight-operate-live-app/SKILL.md` for that route. Continue below when SDK installation is requested or selected.

## Outcome

The finished app must:

- include `ai.ansight:ansight-android` in a local-development build;
- initialize Ansight once from `Application.onCreate()`;
- register automatically on an emulator and expose
  `Ansight.enrollFromQrCode(activity)` from a developer-only UI for physical devices;
- reconnect automatically after the first successful scan;
- keep broad or mutating remote tools out of distributable builds; and
- require no generated JSON, build secret, certificate, or manual app registration.

## Workflow

1. Identify the app module, `Application` class, debug variant, and existing developer menu.
2. Add the aggregate dependency, normally:

```kotlin
dependencies {
    debugImplementation("ai.ansight:ansight-android:1.4.0-preview.1")
}
```

3. Initialize from `Application.onCreate()`:

```kotlin
class MyApplication : Application() {
    override fun onCreate() {
        super.onCreate()
        if (BuildConfig.DEBUG) {
            Ansight.initializeAndActivateDeveloperMode(
                application = this,
                clientName = "Android App",
            )
        }
    }
}
```

4. Add a developer-only action that owns an `Activity`:

```kotlin
Ansight.enrollFromQrCode(activity)
```

5. Do not add a camera permission. The SDK uses Google Code Scanner. The
   aggregate SDK merges ordinary network access and clear-text development
   traffic into the manifest.
6. Keep reflection, secure-storage access, writes, and deletes disabled unless
   the requested development workflow requires them.
7. Create or update `ansight-readme.md` with the scanner location, build
   verification command, and CLI enrollment commands. Do not add an enrollment
   payload or token to it.
8. Run `./gradlew :app:assembleDebug` and relevant tests.
9. Start or reuse the resident CLI host. An emulator registers automatically.
   For a physical device, issue one generic host QR and scan it from the
   developer-only surface:

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

An emulator registers automatically through loopback. For a physical device,
the developer runs `ansight pairing issue --qr` against the resident host and
scans the terminal QR once. The first connection registers the app installation and stores a random
installation id and enrollment state in app-private preferences. Later
developer launches reconnect automatically while the registration remains
active.

If the app already owns a scanner, pass its result through the SDK payload-text
connection API instead of adding a second scanner.

## Recommended Inspection Skills

Use the main router to select the core inspection workflow. Add the native
Android companion only when Android platform semantics are material:

```text
https://www.ansight.ai/skills/agents/ansight-app-inspection.md
https://www.ansight.ai/skills/android/ansight-app-inspection-android.md
```
