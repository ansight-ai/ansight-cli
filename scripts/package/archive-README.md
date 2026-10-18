# Ansight CLI

This archive contains the self-contained CLI, local player, and bundled agent
skills. Keep the extracted files together. Do not copy only `ansight` or
`ansight.exe` out of this directory.

## Start locally

On macOS or Linux, open a terminal in the extracted directory:

```sh
./ansight version
./ansight doctor
./ansight host run --open
```

On Windows, open PowerShell in the extracted directory:

```powershell
.\ansight.exe version
.\ansight.exe doctor
.\ansight.exe host run --open
```

Add this directory to your user PATH to run `ansight` from other directories.
Local tools do not require an account. Sign in with `ansight account login` when
you want to use cloud features. `doctor` explains any missing platform tools;
use `ansight setup android` for Android prerequisites. Apple development
workflows require macOS and the relevant Xcode components.

For an existing recording, run `ansight session serve <session-id>`. For SDK
integration and agent setup, see https://www.ansight.ai/docs/cli and the `skills/`
directory. Invoke commands with `--help` for their options.

## Privacy and permissions

The CLI and local player send pseudonymous usage analytics to PostHog, including
without sign-in. Baseline measurement includes daily activity, hourly engagement,
and installation/update events, linked to an installation identifier. These
analytics events exclude capture contents, logs, prompts, and raw command arguments.

Detailed analytics is off by default and requires sign-in and explicit opt-in.
Run `ansight analytics status` to inspect tracking and
`ansight analytics detailed disable` to disable detailed analytics and remove
unsent detailed events. `ANSIGHT_ANALYTICS_DISABLED=true` also disables detailed
collection. Neither control disables baseline measurement.

Cloud uploads, model-backed workflows, and integrations send the data needed
for the operation you request. Review https://www.ansight.ai/privacy and
https://www.ansight.ai/docs/security before using sensitive app data.

Simulator/emulator enrollment does not need a camera. Physical-device QR
scanning has platform-specific permissions; see
https://www.ansight.ai/docs/sdk/enrollment-permissions.

## Updates and removal

For a manual installation, replace the extracted directory with a complete new
archive and update PATH if its location changes. Stop the running host before
replacing its files. On Windows, use Ctrl+C in the host terminal if graceful
`host stop` is unavailable in your build.

Remove the extracted directory and its PATH entry to remove a manual install.
This does not delete captures, settings, credentials, or cloud records. See
https://www.ansight.ai/docs/cli/uninstall for those separate cleanup steps.

License and third-party notices are in `LICENSE` and `NOTICE`.
Support: https://www.ansight.ai/support
