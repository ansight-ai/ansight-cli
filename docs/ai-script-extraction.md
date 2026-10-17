# AI script extraction

The replay player's **Generate with AI** action creates Maestro YAML and
Appium/WebdriverIO JavaScript from the selected recording period. Both formats
use the configured model provider, as Ansight test generation does.

Generation combines recorded actions, annotations within the period, UI controls
sampled throughout the journey, and up to six retained screenshots. Optional
generation notes describe the starting state, important steps, or expected
outcome. The model uses the programmatic draft as a starting point and adds
evidence-supported selectors, waits, and assertions. Missing evidence remains
marked for review.

Progress is shown while evidence is loaded, the model writes the draft, and the
result is checked. Maestro output is parsed as YAML and checked for the recorded
app ID and supported commands. Appium output is checked for the recorded app ID
and parsed with `node --check`; this does not execute the script or require
WebdriverIO to be installed. Invalid output receives up to two repair attempts.
Provider or validation failures are reported without returning a programmatic
draft as an AI result.

Review the draft and use the separate Validate and Play actions before saving
it to the workspace. Generation does not run the app or prove that the generated
test passes on a device.

The CLI exposes the same analysis through `--ai`:

```sh
ansight task extract SESSION --start 0 --end 45 --format maestro --ai --workspace /path/to/app
ansight task extract SESSION --start 0 --end 45 --format appium --ai --reasoning balanced --workspace /path/to/app
```

Without `--ai`, CLI extraction continues to produce the deterministic recorded
action draft. The replay UI always uses AI for these formats. AI generation
requires a configured model provider; Appium syntax validation also requires
the host's JavaScript runtime.
