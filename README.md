# Ansight

Ansight is a local-host tool for teams and coding agents building iOS and Android
apps. Once connected, development and test sessions on simulators and devices
are captured automatically and kept for review. You can go back through what
happened, inspect the available runtime evidence, and hand the session to the
person or agent investigating it.

A screenshot attached to “trust me, it’s done bro” shows one moment of an
agent's work. Ansight lets you scrub through the session and see what the agent
actually tested. When QA finds a bug, they can mark the relevant moment and
hand over the replay instead of reproducing it just to make a screen recording
and then describing the problem from memory.

## Capture, review, test

- **Capture:** Record sessions from supported simulators and physical devices.
  Screens, interactions, logs, telemetry, and other evidence are available
  according to the capture mode and SDK integration. Enrich sessions with
  custom data from your app.
- **Review:** Scrub through a session in the local player, inspect its evidence,
  and annotate a timeline moment or an area of the screen. Export a portable
  ZIP for handoff.
- **Test:** Your existing coding agent can inspect and drive the live app with
  [`ansight app interact`](https://www.ansight.ai/docs/cli/commands#app-interact).
  For a delegated run, hand a plain-language goal to `ansight app execute` or
  define a UI test with success criteria for Ansight's hosted agent to verify.
  Extract stable steps into repeatable local tasks.

## Free local tools

The CLI and local host need no account or credit card. Local capture, device
tools, `app interact`, session inspection and replay, annotations, ZIP export,
and local tasks are free. Sign in for cloud sharing, build uploads, delegated
agent execution (`app execute` and UI test runs), and remote runners.

Visit the [Ansight website](https://www.ansight.ai), read the
[documentation](https://www.ansight.ai/docs), or follow the
[getting-started guide](https://www.ansight.ai/docs/getting-started).

## Build

Requires .NET SDK 10.0.300 and Node.js 24 or later.

```sh
npm ci
npm run build:player
dotnet build src/Ansight.Cli/Ansight.Cli.csproj
```

See [CONTRIBUTING](CONTRIBUTING.md) for development commands and
[the docs](https://www.ansight.ai/docs) for product guidance.

Licensed under [PolyForm Shield 1.0.0](LICENSE). See [NOTICE](NOTICE) for
third-party notices.
