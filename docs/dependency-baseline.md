# Dependency setup baseline

`ansight.dependencies.json` is the source-controlled baseline for Ansight's
external tools, including dependencies installed through Homebrew, npm, .NET
tools, and Android SDK Manager. Update it when changing the setup used for a
release. Package names describe installation sources; they do not pin packages.

Each entry has an inclusive `versions.min`, an exact `versions.target`, and an
optional exclusive `versions.maxExclusive`. For example, `<4.0.0` covers all
stable 3.x versions. The target must be at least the minimum and below the maximum
when one is declared. Omitting the maximum leaves the range open above the target.
Versions use numeric components, with prerelease and build suffixes supported.

These are advisory expectations, not support requirements. Older or newer tools
can still work. A version notice does not disable a feature, change Doctor's
traffic light, fail `--require`, change the process exit code, or install or
downgrade anything. Missing tools and actual readiness failures remain separate.
Unknown versions are reported explicitly rather than assumed compatible.

The initial baseline records the macOS ARM64 development setup on 2026-10-11.
It is not a claim that every tool or platform combination has been tested.
Minimums initially equal the recorded targets and there are no maximums.
The emulator version came from its installed package metadata; the Android
command-line-tools target is the existing `ansight setup android` default.
Record broader ranges only when there is evidence for them, and update `basis`,
`recordedOn`, `recordedPlatform`, and `notes` to describe that evidence. Individual
entries can override `basis`, as the Android setup default does.

## Production build behavior

The shared `Ansight.Infrastructure` project embeds the JSON bytes as the
`Ansight.DependencyBaseline.json` assembly resource. This includes public and
cloud-enabled builds, all target architectures, and self-contained single-file
releases. The installed program never reads a working-directory or installation
sidecar to get its baseline. Updating the source file changes the next compiled
build, not an already distributed binary.

Release compilation runs `scripts/build/validate-dependency-baseline.mjs` before
building. Node.js is needed on the build machine, as it is for the player assets;
this adds no runtime requirement to the self-contained CLI. Invalid metadata
(including duplicate dependency IDs, unknown fields, malformed versions, and
inverted ranges) fails the build. That authoring check is separate from the
advisory comparison against a user's installed tools.

The build uses the reviewed manifest as-is. It does not replace targets with
whatever happens to be installed on each packaging machine. All release RIDs
therefore carry the same baseline. A SHA-256 of the embedded bytes identifies
that baseline alongside the CLI's existing release identity.

Inspect the embedded baseline without probing or installing dependencies:

```sh
ansight version --json
```

It is also present in `ansight doctor --json` and in the `dependencyBaseline`
section of `ansight doctor --full --json`. Existing executable version probes
include a separate `versionAdvice` with the observed version, declared range,
comparison status and any notice. The quick Doctor reuses Tesseract and scrcpy
version results and probes the available Node, Appium, .NET diagnostic, and
Android tools. Full reports compare the executable versions they collect.
Other baseline entries remain available as release metadata even when their
platform-specific version is not probed. This does not record or overwrite a
separate per-machine setup history.

Validate an edit before building:

```sh
node scripts/build/validate-dependency-baseline.mjs ansight.dependencies.json
node --test scripts/build/validate-dependency-baseline.test.mjs
```
