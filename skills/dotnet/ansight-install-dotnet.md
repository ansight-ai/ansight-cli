---
name: ansight-install-dotnet
description: Install Ansight in a .NET MAUI, Android, iOS, or Mac Catalyst app with automatic local CLI-host registration and one-shot QR enrollment for physical devices. Add the all-in-one package, initialize at app startup, expose the SDK scanner from a developer-only surface, keep remote tools development-only, verify target builds, and inspect the app through the Ansight CLI.
---

# Ansight .NET Install Skill

Use this skill for .NET MAUI, .NET for Android, .NET for iOS, and .NET Mac
Catalyst apps.

## Capture Choice

This skill implements an explicitly selected SDK integration. If the user only wants capture or inspection, explain SDK capture and external physical/virtual device capture before changing the app; preserve any choice already made. External capture needs no SDK, rebuild, or pairing QR. Follow `https://www.ansight.ai/skills/agents/ansight-operate-live-app/SKILL.md` for that route. Continue below when SDK installation is requested or selected.

## Workflow

1. Identify the app project, target frameworks, startup path, debug guard, and
   existing developer menu.
2. For MAUI, install the all-in-one package:

```shell
dotnet add package Ansight.Maui --prerelease
```

   Then initialize before `builder.Build()`:

```csharp
builder.UseMauiApp<App>();

#if DEBUG
builder.UseAnsight<App>();
#endif
```

   For non-MAUI apps, install `Ansight` and initialize:

```csharp
#if DEBUG
var options = Options.CreateBuilder()
    .WithAnsightSdk()
    .Build();

Runtime.InitializeAndActivate(options);
#endif
```

3. Add a developer-only scanner action:

```csharp
var result = await Runtime.HostConnection.ConnectAsync(
    HostConnectionRequest.QrCode());
```

4. If the app already owns a QR scanner, pass its text through
   `HostConnectionRequest.PayloadText(...)`.
5. Keep all-in-one and broad remote-tool packages in local-development builds.
   Use a protected Release/CI policy for distributable builds.
6. Add only platform privacy declarations needed by the scanner and local
   network flow. Do not add an embedded resource, MSBuild-generated enrollment
   file, certificate, or host address.
7. Create or update `ansight-readme.md` with the scanner location, tool policy,
   build checks, and CLI enrollment commands. Do not include an enrollment payload or token.
8. Run `dotnet build` and relevant tests for every target changed.
9. Start or reuse the resident CLI host. A simulator, emulator, Mac Catalyst,
   or desktop build registers automatically. For a physical device, issue one
   generic host QR and scan it from the developer-only surface:

```sh
ansight host run
ansight pairing issue --qr
ansight session list --connected --app-id <app-id> --json
ansight app tools <session-id> --detail summary --include-unavailable --max-results 50 --json
```

   Do not require `ansight app register` before the first connection. After the
   SDK has supplied its real App ID, optionally link that discovered app to its
   repository with `ansight app register <app-id> --codebase <repository-path>`.

## Enrollment Behavior

Host-local targets register through automatic loopback and do not need a scan.
The first physical-device scan registers the installation and stores a random installation id
and enrollment state in app-private storage. `HostConnectionRequest.Auto()` and
normal host auto-probe reconnect it on later developer launches.

## Recommended Inspection Skills

Use the main router to select the core inspection workflow. Add the .NET
companion only when MAUI or .NET-specific semantics are material:

```text
https://www.ansight.ai/skills/agents/ansight-app-inspection.md
https://www.ansight.ai/skills/dotnet/ansight-app-inspection-dotnet.md
```
