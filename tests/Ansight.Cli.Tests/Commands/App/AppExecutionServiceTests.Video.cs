namespace Ansight.Cli.Tests.Commands.App;

public sealed partial class AppExecutionServiceTests
{
    [Fact]
    public async Task AppClosesAfterRecorderStopsBeforeVideoIsSaved()
    {
        var recorder = new RecordingVideoRecorder();
        var closed = false;
        await AppExecutionVideoRunner.RunAsync(
            async () => await StartVideoAsync("run", recorder, "always"),
            () => Task.FromResult(CreateRunResult()),
            _ => { Assert.True(closed); return new("saved.json", null); },
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            () => { Assert.Equal(1, recorder.Stops); closed = true; return Task.CompletedTask; });
        Assert.True(closed);
    }

    [Theory]
    [InlineData(SimulatorAgentRunStatus.Succeeded, "always", "completed")]
    [InlineData(SimulatorAgentRunStatus.Succeeded, "on-failure", "discarded")]
    [InlineData(SimulatorAgentRunStatus.Failed, "on-failure", "completed")]
    [InlineData(SimulatorAgentRunStatus.Cancelled, "on-failure", "completed")]
    public async Task AppExecutionRecordsBeforeActionsAndRetainsVideoInAudit(
        SimulatorAgentRunStatus status, string retention, string expectedStatus)
    {
        var original = CreateRunResult() with { Status = status };
        var recorder = new RecordingVideoRecorder();
        SimulatorAgentRunAudit? persisted = null;
        var result = await AppExecutionVideoRunner.RunAsync(
            async () => await StartVideoAsync(original.Audit.RunId, recorder, retention),
            () =>
            {
                Assert.Equal(1, recorder.Starts);
                Assert.Equal(0, recorder.Stops);
                return Task.FromResult(original);
            },
            audit =>
            {
                Assert.Equal(1, recorder.Stops);
                persisted = audit;
                return new("saved.json", null);
            },
            new CliOutput(false, TextWriter.Null, TextWriter.Null));

        Assert.Equal(original.Status, result.Status);
        Assert.Equal(expectedStatus, result.Audit.Video?.Status);
        Assert.Equal(original.Audit.RunId, result.Audit.Video?.RunId);
        Assert.Equal(result.Audit, persisted);
        Assert.Equal("saved.json", result.AuditFilePath);
        Assert.False(Directory.Exists(Path.GetDirectoryName(recorder.OutputPath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppExecutionFinalizesVideoWhenExecutionThrows(bool cancelled)
    {
        var recorder = new RecordingVideoRecorder();
        using var diagnostics = new StringWriter();
        var expected = cancelled ? (Exception)new OperationCanceledException() : new IOException("Execution failed");
        var actual = await Record.ExceptionAsync(() => AppExecutionVideoRunner.RunAsync(
            async () => await StartVideoAsync("run", recorder, "on-failure"),
            () => Task.FromException<SimulatorAgentRunResult>(expected),
            _ => throw new InvalidOperationException("No audit was produced"),
            new CliOutput(true, TextWriter.Null, diagnostics)));

        Assert.Same(expected, actual);
        Assert.Equal(1, recorder.Stops);
        Assert.Contains("Video: completed", diagnostics.ToString());
        Assert.Contains("Replay video:", diagnostics.ToString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(recorder.OutputPath)));
    }

    [Fact]
    public async Task AppExecutionRecordingStartupFailureDoesNotChangeOutcome()
    {
        var recorder = new RecordingVideoRecorder { FailStart = true };
        var original = CreateRunResult();
        var result = await AppExecutionVideoRunner.RunAsync(
            async () => await StartVideoAsync(original.Audit.RunId, recorder, "always"),
            () => Task.FromResult(original),
            _ => new("saved.json", null),
            new CliOutput(false, TextWriter.Null, TextWriter.Null));
        Assert.Equal(original.Status, result.Status);
        Assert.Equal("failed", result.Audit.Video?.Status);
        Assert.Equal(0, recorder.Stops);
    }

    [Fact]
    public async Task AppExecutionWithVideoDisabledPreservesOriginalResult()
    {
        var original = CreateRunResult();
        var result = await AppExecutionVideoRunner.RunAsync(
            () => Task.FromResult<TestVideoCapture?>(null),
            () => Task.FromResult(original),
            _ => throw new InvalidOperationException("No additional persistence is required"),
            new CliOutput(false, TextWriter.Null, TextWriter.Null));
        Assert.Same(original, result);
    }

    [Fact]
    public async Task AppExecutionAuditSaveFailurePreservesVideoAndOutcome()
    {
        var original = CreateRunResult();
        var result = await AppExecutionVideoRunner.RunAsync(
            async () => await StartVideoAsync(original.Audit.RunId, new RecordingVideoRecorder(), "always"),
            () => Task.FromResult(original),
            _ => throw new IOException("Audit unavailable"),
            new CliOutput(false, TextWriter.Null, TextWriter.Null));
        Assert.Equal(original.Status, result.Status);
        Assert.Equal("completed", result.Audit.Video?.Status);
        Assert.Equal("Audit unavailable", result.AuditPersistenceError);
    }

    private static Task<TestVideoCapture> StartVideoAsync(string runId, RecordingVideoRecorder recorder, string retention)
        => TestVideoCapture.StartAsync(runId, "session-1", "device-1", "android", true,
            new(retention), recorder, (snapshot, path) =>
            {
                Assert.Equal(runId, snapshot.RunId);
                Assert.True(File.Exists(path));
                return OperationResult.Success("Saved");
            }, CancellationToken.None);

    private sealed class RecordingVideoRecorder : ITestVideoRecorder, ITestVideoRecording
    {
        public string Source => "fixture";
        public bool FailStart { get; init; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public string? OutputPath { get; private set; }

        public Task<ITestVideoRecording> StartAsync(TestVideoRecordingRequest request, CancellationToken cancellationToken)
        {
            Starts++;
            if (FailStart) throw new IOException("Recorder unavailable");
            OutputPath = request.OutputPath;
            return Task.FromResult<ITestVideoRecording>(this);
        }

        public Task<TestVideoRecordingCompletion> StopAsync()
        {
            Stops++;
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "test-recording.mp4"), OutputPath!);
            return Task.FromResult(new TestVideoRecordingCompletion(true));
        }
    }
}
