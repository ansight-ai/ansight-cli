# Tests

A test describes a user journey and its observable finish line. Ansight gives
the instructions to an agent, the agent operates the app, and a separate
validation step decides whether the expected state was reached.

Read the full [Ansight workspace tests guide](https://www.ansight.ai/docs/workspace/tests).

```sh
ansight workspace add test . onboarding.smoke \
  --app-id com.example.app \
  --assertion "The signed-in home screen is visible"
```

```json
{
  "schemaVersion": 1,
  "id": "onboarding.smoke",
  "name": "Onboarding smoke test",
  "appId": "com.example.app",
  "prompt": "Launch the app and complete onboarding as a new user.",
  "validation": {
    "prompt": "Inspect the final app state.",
    "assertions": ["The signed-in home screen is visible"]
  },
  "requiredSecrets": ["TEST_USER_PASSWORD"]
}
```

`requiredSecrets` contains aliases only; values are resolved at run time.

```sh
ansight test validate .
ansight test run . onboarding.smoke
```

Virtual-device windows are shown by default. For a windowless local or CI run,
add `--headless` explicitly; `--json` alone does not select it:

```sh
ansight test run . onboarding.smoke --headless --json
ansight test run-all . --headless --json
```

This applies to every target in multi-device runs. iOS skips opening
Simulator.app; newly started Android emulators use `-no-window`. Existing
windows and physical devices are left alone. `--headless` is a CLI option,
not a field in the test definition.
