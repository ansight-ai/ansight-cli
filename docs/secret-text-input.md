# Type saved app secrets

Text input can resolve a saved app secret locally without placing its value in
shell history or command output. It uses the existing app-scoped vault and
explicit environment-variable fallback; it does not need an AI run or account.

```sh
ansight ui type --automation-id Email --value '@secret:com.example.app/login.email'
ansight ui type --session session-001 --automation-id Password --value '@secret:com.example.app/login.password'
```

A reference has the form `@secret:<app-id>/<alias>`; the app ID is literal, not a
shell variable. It selects the app's secret store and, for semantic UI input,
the connected app. An explicitly targeted session must belong to that app.
The shorter `@secret:<alias>` and `--secret <alias>` forms use the selected
session's app (or require `--app-id` for raw device input).

Both forms select one alias. Use exactly one of `--value` or `--secret`.
`--append` also works with secrets. The selected live session determines the
secret's App ID. A missing secret or ambiguous session fails before typing.
`--value '@@secret:literal'` types the literal text `@secret:literal`.

For raw device input, first focus the intended app's field, then specify the
app whose secret store to use:

```sh
ansight input text android emulator-5554 --app-id com.example.app --secret login.password
ansight input text ios simulator-udid --value '@secret:com.example.app/login.password'
```

The raw command's `--app-id` selects the secret store; it does not launch the app
or focus a field. Prefer semantic `ui type` for a verified target. Secret input
returns status and the alias instead of UI contents or backend diagnostic text.
Normal session capture still applies: text displayed by the app can appear in
screenshots, visual trees, or app-generated logs. Android text travels over ADB
standard input so it does not appear in host process arguments.
