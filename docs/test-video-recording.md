# Execution video recording (v1)

Tests and app executions capture a silent, continuous replay video by default:

```sh
ansight app execute SESSION_ID --prompt "Complete checkout and verify the receipt"
ansight app execute --app-id com.example.app --platform ios --device-id SIMULATOR_UDID --prompt "Verify onboarding"
ansight test run /path/to/workspace checkout
ansight test run-all /path/to/workspace --video-retention on-failure
```

Use `--no-video` to skip recording. `--video-retention always|on-failure|off`
overrides a saved test setting for this invocation. Without flags, tests preserve
an explicit saved setting and otherwise retain video for every outcome. App
executions default to `always`. Batch overrides apply to every test/target.
`--video` is removed from these commands; both the CLI and resident host must be
updated. Do not combine `--no-video` with `always` or `on-failure` retention.

```sh
ansight test run /path/to/workspace checkout --no-video
ansight app execute SESSION_ID --prompt "Verify the screen" --no-video
```

A saved test can override retention, including for a deterministic task:

```yaml
appId: com.example.app
taskId: checkout
video:
  retention: always
```

`always` is the default. Set `off` to disable recording in a saved test. `on-failure` records the whole test and discards the video
if it passes. Direct `task run` does not record; wrap it in a saved test.

## Startup

For a launched agentic test or app execution, account/team resolution and AI
brokering run concurrently with app launch and session connection. Execution
begins once both are ready. The startup timeline retains each branch's actual
start time and duration, so their bars overlap. Launch failure or cancellation
cancels pending preparation and closes any prepared metered run. Task-only tests
still skip AI setup.

## Native capture and compression

- iOS Simulator on macOS: Apple's `xcrun simctl io <udid> recordVideo --codec=h264`.
- Android devices/emulators on macOS/Linux: scrcpy with ADB. Scrcpy uses Android's
  native display capture and MediaCodec encoder. Ansight starts a dedicated,
  noninteractive recorder, independent of live preview.
- Simulator compression: the bundled `libAnsightSessionVideoMac.dylib` uses
  `AVAssetReader`, `AVAssetReaderVideoCompositionOutput`, `AVMutableVideoComposition`
  and `AVAssetWriter`/`AVAssetWriterInput`. AVFoundation uses the system H.264
  encoding stack (VideoToolbox). No FFmpeg runtime dependency is required.

Defaults: H.264, maximum 1280-pixel long edge, maximum 24 fps, 1.2 Mbps target,
no audio. Android applies limits during capture; Simulator records natively then
transcodes before retention. Content determines actual bitrate and size. Raw
Simulator files are temporary and removed after the compressed replay is saved.
These are display recordings of motion, not stitched screenshots. Physical iOS,
Windows hosts and desktop apps are unsupported in this v1 path.

Capture starts after target/session selection and before execution actions, and
stops before optional app termination. It does not cover installation or initial
launch. Native startup acknowledgment is awaited. Finalization sends SIGINT and
waits up to 20 seconds, independently of execution cancellation. Final idle time
is retained. Compression explicitly preserves the captured media duration and
writes a seekable MP4 with its index at the front.

Test targets and app executions with `--close-app-on-completion` close the app
after the recorder stops, before compression and metering. A completed test's
replay ends at its video boundary; later profiler shutdown diagnostics remain
available without adding empty playback time.

## Replay integration

Recordings are first-class session media. `session.videoRecordings` holds each
run's recording ID, UTC start, media duration, dimensions, source, status and byte
size. MP4s live at `session-video/<recording-id>.mp4`, separate from artifacts.
Multiple executions can contribute distinct recordings to a reused session.

Open the session replay, or **Tests → History → Open recorded replay**. Play/pause
advances the replay timeline; scrubbing seeks within the continuous recording.
Logs, touches and other evidence follow that playhead. Screenshots remain
available outside recorded intervals, for explicit visual-tree inspection, and
as a fallback if video cannot load. Capture boundaries are host-observed, so
alignment is approximate rather than a device-clock synchronization guarantee.

The CLI prints recording status, ID and size. JSON exposes it at `result.video`
for `test run` and `result.audit.video` for `app execute`. The local player serves
MP4s through `api/sessions/<session-id>/video/<recording-id>` with HTTP Range
support. Normal session archives include videos even with artifacts excluded;
import, cache compaction and session deletion retain the same media lifecycle.
A trace-only export does not include the MP4.

Sanitized exports omit video because screenshot redaction cannot redact its
continuous pixels. Partial timeline extraction/trimming through a recording is
rejected explicitly in v1; select whole recordings. Whole-recording extraction
and removal are supported.

Recording failures do not change the assertion outcome. Status is `completed`,
`partial`, `failed`, `unsupported` or `discarded`. Only validated, finalized media
is published to the replay. Finalization/storage failures report the temporary
capture directory for recovery; failed/unsupported status remains in the run
audit. Abrupt host termination cannot guarantee finalization.

## Session summarisation

Local session summaries (`session summary SESSION_ID --local` and the local
player) and section summaries read frames directly from retained replay video.
Up to five frames cover the initial/final states and points of interest selected
from navigation, interactions, errors and annotations. Frame timestamps respect
the selected section and variable-frame-rate sample timing. Both visual analysis
and OCR use these decoded frames, and the saved analysis records their provenance.
Temporary frames are removed when analysis finishes or fails.

macOS uses native `AVAssetImageGenerator`; Windows uses native
`MediaComposition.GetThumbnailAsync`. Linux requires FFmpeg on PATH for frame
decoding. This decoder dependency is separate from native video capture.
Sessions without video retain screenshot-based summaries. When video exists but
is missing or cannot be decoded, summarisation reports an error instead of
silently switching to screenshots. This applies to local analysis; hosted shared
session analysis uses its separate backend.

## Verification

Focused tests cover retention, task/CLI execution, cancellation, invalid media,
first-class persistence, archive round trips, sanitized export, native encoding,
resolution/duration, and recording selection across timeline gaps. The native
recorder smoke test is opt-in:

```sh
ANSIGHT_TEST_VIDEO_DEVICE_ID=<exact-device-id> \
ANSIGHT_TEST_VIDEO_PLATFORM=ios \
dotnet test tests/Ansight.Host.Tests/Ansight.Host.Tests.csproj \
  --filter FullyQualifiedName~NativeTestVideoRecorderSmokeTests
```

Use `android` with an ADB serial. This captures the already selected display for
1.5 seconds without launching or controlling an app.
