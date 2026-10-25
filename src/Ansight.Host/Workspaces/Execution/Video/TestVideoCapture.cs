using Ansight.Host.Sessions.Video;

namespace Ansight.Host.Workspaces.Execution;

// One capture belongs to one execution, even when its session is reused by later runs.
internal sealed class TestVideoCapture
{
    private readonly string directory;
    private readonly string videoPath;
    private readonly TestVideoOptions options;
    private readonly Func<SessionVideoRecording, string, OperationResult> saveRecording;
    private readonly Lock gate = new();
    private ITestVideoRecording? recording;
    private TestVideoResult result;
    private Task<TestVideoResult>? completion;
    private Task<TestVideoRecordingCompletion>? stopping;
    private ISessionVideoTranscoder? transcoder;

    private TestVideoCapture(string runId, string sessionId, string? deviceIdentifier,
        TestVideoOptions options, Func<SessionVideoRecording, string, OperationResult> saveRecording)
    {
        this.options = options;
        this.saveRecording = saveRecording;
        directory = Path.Combine(Path.GetTempPath(), "ansight-test-video", Guid.NewGuid().ToString("N"));
        videoPath = Path.Combine(directory, "recording.mp4");
        var now = DateTimeOffset.UtcNow;
        result = new(runId, sessionId, "starting", "Starting video recording.", "", deviceIdentifier, now, now);
    }

    internal static async Task<TestVideoCapture> StartAsync(string runId, string sessionId,
        string? deviceIdentifier, string platform, bool isVirtual, TestVideoOptions options,
        ITestVideoRecorder recorder, Func<SessionVideoRecording, string, OperationResult> saveRecording,
        CancellationToken cancellationToken, ISessionVideoTranscoder? transcoder = null)
    {
        var capture = new TestVideoCapture(runId, sessionId, deviceIdentifier, options, saveRecording);
        capture.transcoder = transcoder;
        try
        {
            Directory.CreateDirectory(capture.directory);
            if (string.IsNullOrWhiteSpace(deviceIdentifier))
                throw new NotSupportedException("Video recording needs an exact device identifier. Select a test target or a session with a native device ID.");
            capture.recording = await recorder.StartAsync(new(platform, deviceIdentifier, isVirtual, capture.videoPath),
                cancellationToken).ConfigureAwait(false);
            capture.result = capture.result with { Source = capture.recording.Source, Status = "recording", StartedUtc = DateTimeOffset.UtcNow };
        }
        catch (OperationCanceledException)
        {
            capture.DeleteWorkingFiles();
            throw;
        }
        catch (Exception exception)
        {
            capture.result = capture.result with
            {
                Status = exception is NotSupportedException ? "unsupported" : "failed",
                Message = exception.Message
            };
        }
        return capture;
    }

    public Task<TestVideoResult> CompleteAsync(bool testPassed)
    {
        lock (gate) return completion ??= CompleteCoreAsync(testPassed);
    }

    // Release the device before compression, persistence and billing finish.
    public Task<TestVideoRecordingCompletion> StopAsync()
    {
        lock (gate) return stopping ??= StopCoreAsync();
    }

    private async Task<TestVideoRecordingCompletion> StopCoreAsync()
    {
        try
        {
            return recording is null ? new(false, result.Message)
                : await recording.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new(false, exception.Message);
        }
        finally
        {
            result = result with { CompletedUtc = DateTimeOffset.UtcNow };
        }
    }

    private async Task<TestVideoResult> CompleteCoreAsync(bool testPassed)
    {
        if (recording is null)
        {
            DeleteWorkingFiles();
            return result with { CompletedUtc = DateTimeOffset.UtcNow };
        }
        try
        {
            var stopped = await StopAsync().ConfigureAwait(false);
            if (testPassed && options.Retention == "on-failure")
            {
                DeleteWorkingFiles();
                return result with { Status = stopped.IsComplete ? "discarded" : "failed",
                    Message = stopped.IsComplete ? "Recording discarded because the run passed." : stopped.Error ?? "Video finalization failed." };
            }
            _ = SessionVideoMp4TimingInspector.ReadPresentationTimesUs(videoPath);
            if (stopped.IsComplete && stopped.CapturedDuration is { } duration)
                SessionVideoMp4TimingInspector.ExtendFinalFrame(videoPath, duration);
            var retainedPath = videoPath;
            // Android encodes at these limits on-device. Simulator requires native transcoding.
            if (recording.Source == "simctl")
            {
                if (transcoder is null) throw new NotSupportedException("The native Simulator video compressor is unavailable.");
                retainedPath = Path.Combine(directory, "compressed.mp4");
                var mediaDuration = SessionVideoMp4TimingInspector.ReadDuration(videoPath);
                await transcoder.TranscodeAsync(videoPath, retainedPath, mediaDuration).ConfigureAwait(false);
                if (Math.Abs((SessionVideoMp4TimingInspector.ReadDuration(retainedPath) - mediaDuration).TotalMilliseconds) > 50)
                    throw new InvalidDataException("Compression changed the recording duration.");
            }
            var info = SessionVideoMp4TimingInspector.ReadVideoInfo(retainedPath);
            _ = SessionVideoMp4TimingInspector.ReadPresentationTimesUs(retainedPath);
            var metadata = new SessionVideoRecording(result.RunId, result.RunId, result.StartedUtc,
                info.Duration.Ticks / 10, info.Width, info.Height, new FileInfo(retainedPath).Length,
                result.Source, stopped.IsComplete ? "completed" : "partial");
            metadata.Validate();
            var saved = saveRecording(metadata, retainedPath);
            if (!saved.IsSuccess) throw new IOException(saved.Message);
            result = result with { Status = metadata.Status, RecordingId = metadata.RecordingId,
                Path = metadata.EntryPath, ByteSize = metadata.ByteSize, Width = metadata.Width, Height = metadata.Height,
                DurationUs = metadata.DurationUs,
                Message = stopped.IsComplete ? "Video saved in the session replay." : stopped.Error ?? "Recording ended early." };
            DeleteWorkingFiles();
        }
        catch (Exception exception)
        {
            result = result with { Status = "failed", RecordingId = null, Path = null,
                Message = $"Could not finalize replay video: {exception.Message} Capture files remain at '{directory}'." };
        }
        return result;
    }

    private void DeleteWorkingFiles()
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
