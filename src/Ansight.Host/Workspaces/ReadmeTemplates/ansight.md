# Ansight workspace

This folder is the app's source-controlled Ansight playbook: reusable tests,
automations, privacy rules, and deterministic trends monitoring.

Read the full [Ansight workspace guide](https://www.ansight.ai/docs/workspace).

## The simple map

- `tests/` describes user journeys an agent should perform and verify.
- `tasks/` contains deterministic recipes that a person or agent starts by ID.
- `triggers/` contains small reactions to app or session events.
- `trends/` defines event-bounded telemetry metrics, budgets, and regression monitoring.
- `sanitizers/` removes or redacts sensitive content from exported sessions.
- `schema/` contains the machine-readable contracts for workspace JSON.

Each trends definition owns its span and the metrics evaluated in
that window. Add `regression` to a metric when Ansight should compare it with
historical runs as well as enforce its per-run budget.

## Start here

```sh
ansight workspace add test . onboarding.smoke --app-id com.example.app
ansight workspace add task . home.verify --app-id com.example.app
ansight workspace add trigger . diagnostics.capture-errors \
  --app-id com.example.app \
  --event-kind session.log.received
ansight workspace add sanitizer . team-safe
```

Validate JSON definitions and TypeScript modules before committing:

```sh
ansight test validate .
npx tsc -p ansight/tasks/tsconfig.json
npx tsc -p ansight/triggers/tsconfig.json
npx tsc -p ansight/sanitizers/tsconfig.json
```

Device-launching commands show simulator/emulator windows by default. Use
`--headless` on a test run or other launch command to opt out; CI and `--json`
do not imply it. Existing windows and physical devices are left alone. See
[headless device launches](https://www.ansight.ai/docs/cli/commands#headless-device-launches).

Keep secret names in definitions, but never commit secret values. These README
files are starting points for your team; edit them freely. Workspace initialization
preserves customized support files unless `--force` is supplied.
