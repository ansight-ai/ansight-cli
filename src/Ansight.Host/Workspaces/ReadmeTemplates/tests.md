# Tests

A test describes a user journey and its observable finish line. Ansight gives
the instructions to an agent, which operates the app and verifies each requested
check using evidence. Put actions and checks together in the prompt; a separate
validation block is optional.

Read the full [Ansight workspace tests guide](https://www.ansight.ai/docs/workspace/tests).

```sh
ansight workspace add test . onboarding.smoke \
  --app-id com.example.app \
  --assertion "The signed-in home screen is visible"
```

```yaml
schemaVersion: 1
id: onboarding.smoke
name: Onboarding smoke test
appId: com.example.app
hintTasks:
  - onboarding.open-sign-in
  - onboarding.submit-sign-in
prompt: |-
  Launch the app and complete onboarding as a new user.
  Verify that the signed-in home screen is visible.
requiredSecrets:
  - TEST_USER_PASSWORD
```

`requiredSecrets` contains aliases only; values are resolved at run time.
`hintTasks` is optional. It names up to five repository task IDs to preload and
suggest to the agent. A hint is used only when its starting state and scope fit;
the task's own assertions provide evidence for steps it verifies.
Use `@task/ID` for an exact workspace task or `@selector/ID` for an exact
automation ID inside the prompt. The YAML editor completes tasks and recorded
selectors and shows their details on hover. Percent-encode special characters
in IDs. New tests use `.yaml`; existing `.json` tests remain valid.

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
