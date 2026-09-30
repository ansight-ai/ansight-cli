using Ansight.Host.UiAutomation;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class AppInteractionContextTests
{
    [Fact]
    public async Task UnsettledEvidenceStopsInputUntilAutomaticRecoveryWithoutReplaying()
    {
        var backend = new RecordingBackend();
        var context = new AppInteractionContext(backend);
        await context.ExecuteAsync(new("ready", "snapshot"));
        backend.Unsettled = true;
        var action = await context.ExecuteAsync(new("tap", "tap", 0.5, 0.5));
        Assert.Equal("ui_unsettled", action.Error);
        Assert.True(action.InputSucceeded);
        Assert.NotNull(action.Screenshot);
        var blocked = await context.ExecuteAsync(new("blocked", "type", Value: "no"));
        Assert.Equal("ui_unsettled", blocked.Error);
        Assert.Null(blocked.InputSucceeded);
        Assert.NotNull(blocked.Screenshot);
        Assert.Equal(1, backend.InputCount);
        backend.Unsettled = false;
        var recovered = await context.ExecuteAsync(new("recovered", "type", Value: "yes"));
        Assert.True(recovered.Succeeded);
        Assert.Equal(2, backend.InputCount);
    }

    [Fact]
    public async Task WarmActionsCaptureExactlyOnceAndRetainEvidenceWithoutAnyCaptureCommand()
    {
        var backend = new RecordingBackend();
        var session = new AppInteractionContext(backend);
        var ready = await session.ExecuteAsync(new("ready", "snapshot"));
        for (var index = 0; index < 20; index++)
        {
            var result = await session.ExecuteAsync(new(index.ToString(), "tap", 0.5, 0.7));
            Assert.True(result.Succeeded);
            Assert.Equal($"frame-{index + 1}", result.PreviousScreenshot!.FrameId);
            Assert.Equal($"frame-{index + 2}", result.Screenshot!.FrameId);
            Assert.True(result.Timing.TotalMs >= result.Timing.InputMs);
        }
        Assert.NotNull(ready.Screenshot);
        Assert.Equal(20, backend.InputCount);
        Assert.Equal(21, backend.CaptureCount);
        Assert.Equal(21, backend.Records.Count);
        Assert.All(backend.Records, record => Assert.NotNull(record.Screenshot));
    }

    [Fact]
    public async Task InputFailureStillAutomaticallyCapturesTheResult()
    {
        var backend = new RecordingBackend { InputFailure = true };
        var result = await new AppInteractionContext(backend).ExecuteAsync(new("1", "tap", 0.5, 0.5));
        Assert.False(result.Succeeded);
        Assert.False(result.InputSucceeded);
        Assert.Equal("input_failed", result.Error);
        Assert.NotNull(result.PreviousScreenshot);
        Assert.NotNull(result.Screenshot);
        Assert.Single(backend.Records);
    }

    [Fact]
    public async Task CaptureFailureBlocksBlindInputThenRecoversAutomatically()
    {
        var backend = new RecordingBackend { CaptureFailure = true };
        var session = new AppInteractionContext(backend);
        var failed = await session.ExecuteAsync(new("1", "tap", 0.5, 0.5));
        Assert.Equal("evidence_unavailable", failed.Error);
        Assert.Equal(0, backend.InputCount);
        backend.CaptureFailure = false;
        var recovered = await session.ExecuteAsync(new("2", "tap", 0.5, 0.5));
        Assert.True(recovered.Succeeded);
        Assert.Equal(1, backend.InputCount);
        Assert.Equal(3, backend.CaptureCount);
    }

    [Fact]
    public async Task PostActionCaptureFailureDoesNotReuseOldScreenshotOrRepeatInput()
    {
        var backend = new RecordingBackend();
        var session = new AppInteractionContext(backend);
        await session.ExecuteAsync(new("ready", "snapshot"));
        backend.CaptureFailure = true;
        var failed = await session.ExecuteAsync(new("1", "tap", 0.5, 0.5));
        Assert.True(failed.InputSucceeded);
        Assert.False(failed.Succeeded);
        Assert.Null(failed.Screenshot);
        var blocked = await session.ExecuteAsync(new("2", "tap", 0.5, 0.5));
        Assert.Null(blocked.InputSucceeded);
        Assert.Equal(1, backend.InputCount);
    }

    [Fact]
    public async Task ConcurrentCallersAreSerializedIncludingTheirEvidence()
    {
        var backend = new RecordingBackend { YieldDuringInput = true };
        var session = new AppInteractionContext(backend);
        await session.ExecuteAsync(new("ready", "snapshot"));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            session.ExecuteAsync(new(index.ToString(), "tap", 0.5, 0.5))));
        Assert.Equal(1, backend.MaximumConcurrentInputs);
        Assert.Equal(21, backend.CaptureCount);
    }

    [Fact]
    public async Task DisconnectedSessionNeverSendsInput()
    {
        var backend = new RecordingBackend { Disconnected = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AppInteractionContext(backend).ExecuteAsync(new("1", "tap", 0.5, 0.5)));
        Assert.Equal(0, backend.InputCount);
        Assert.Equal(0, backend.CaptureCount);
    }

    [Theory]
    [InlineData(-0.1, 0.5)]
    [InlineData(0.5, 1.1)]
    [InlineData(double.NaN, 0.5)]
    [InlineData(double.PositiveInfinity, 0.5)]
    public async Task InvalidCoordinatesAreRejectedBeforeInputOrCapture(double x, double y)
    {
        var backend = new RecordingBackend();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new AppInteractionContext(backend).ExecuteAsync(new("1", "tap", x, y)));
        Assert.Equal(0, backend.InputCount);
        Assert.Equal(0, backend.CaptureCount);
    }

    [Fact]
    public void RejectsMissingAndIrrelevantParameters()
    {
        Assert.Throws<ArgumentException>(() => new AppInteractionRequest("1", "tap").Validate());
        Assert.Throws<ArgumentException>(() => new AppInteractionRequest("1", "snapshot", X: 0.5).Validate());
        Assert.Throws<ArgumentException>(() => new AppInteractionRequest("1", "type").Validate());
        Assert.Throws<ArgumentException>(() => new AppInteractionRequest("1", "tap", 0.5, 0.5, Value: "unexpected").Validate());
        Assert.Throws<ArgumentException>(() => new AppInteractionRequest("1", "swipe", 0.5, 0.5).Validate());
        Assert.Throws<ArgumentException>(() => new AppInteractionRequest("1", "pinch", 0.5, 0.5, Scale: 5).Validate());
        Assert.Throws<ArgumentException>(() => new AppInteractionRequest("1", "host stop").Validate());
    }

    private sealed class RecordingBackend : IAppInteractionBackend
    {
        private int concurrentInputs;
        public string SessionId => "session-1";
        public AppInteractionUi? Ui => null;
        public int InputCount { get; private set; }
        public int CaptureCount { get; private set; }
        public int MaximumConcurrentInputs { get; private set; }
        public bool CaptureFailure { get; set; }
        public bool Unsettled { get; set; }
        public bool InputFailure { get; set; }
        public bool Disconnected { get; set; }
        public bool YieldDuringInput { get; set; }
        public List<AppInteractionResult> Records { get; } = [];

        public void EnsureConnected()
        {
            if (Disconnected) throw new InvalidOperationException("Disconnected.");
        }

        public async Task<UiInputResult> ExecuteAsync(AppInteractionRequest request, CancellationToken cancellationToken)
        {
            InputCount++;
            concurrentInputs++;
            MaximumConcurrentInputs = Math.Max(MaximumConcurrentInputs, concurrentInputs);
            if (YieldDuringInput) await Task.Yield();
            concurrentInputs--;
            return new(!InputFailure, "test", "Input attempted.");
        }

        public Task<AppInteractionScreenshot?> CaptureAsync(string actionId, CancellationToken cancellationToken, bool afterInput = false)
        {
            CaptureCount++;
            return Task.FromResult(CaptureFailure ? null : new AppInteractionScreenshot(
                $"frame-{CaptureCount}", $"/tmp/frame-{CaptureCount}.png", DateTimeOffset.UtcNow, 390, 844,
                new(Unsettled ? "timed_out" : "stable", 3, 400)));
        }

        public void Record(AppInteractionResult result) => Records.Add(result);
        public RepositoryTaskCatalog ListTasks() => throw new NotSupportedException();
        public Task<RepositoryTaskRunResult> RunTaskAsync(string taskId, JsonObject? input, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
