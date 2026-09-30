using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class ExternalCaptureDurationTests
{
    [Fact]
    public void HistoricalInstrumentsCaptureUsesTraceCompletionAfterMetricsRecovery()
    {
        using var environment = new TestEnvironment();
        var createdUtc = DateTimeOffset.Parse("2026-09-25T02:48:00Z");
        var completedUtc = DateTimeOffset.Parse("2026-09-25T02:50:09Z");
        var recoveryUtc = DateTimeOffset.Parse("2026-09-25T04:27:54Z");
        var properties = new JsonObject
        {
            ["instruments"] = new JsonObject { ["completedUtc"] = completedUtc }
        };

        Assert.Equal(completedUtc, SessionCaptureEndResolver.Resolve("device", true,
            properties, createdUtc, recoveryUtc));
        Assert.Equal(recoveryUtc, SessionCaptureEndResolver.Resolve("sdk", true,
            properties, createdUtc, recoveryUtc));

        var store = new SessionCaptureStore(environment.ApplicationPaths);
        store.Save(new AppSessionSnapshot
        {
            SessionId = "legacy-external-capture-001",
            AppId = "test.duration",
            ClientName = "Test",
            RemoteAddress = "host-device",
            CreatedUtc = createdUtc,
            ConfigId = null,
            Status = "Completed",
            LastUpdatedUtc = recoveryUtc,
            IsHistorical = true,
            CaptureSource = "device",
            CustomProperties = properties,
            MetricChannels = [],
            Metrics = []
        });
        var reloaded = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        Assert.Equal(completedUtc, reloaded.GetSessionSummaries().Single().LastUpdatedUtc);
        Assert.True(reloaded.TryGetSessionSnapshot("legacy-external-capture-001", out var snapshot));
        Assert.Equal(completedUtc, snapshot!.LastUpdatedUtc);
    }

    [Fact]
    public void CompletedDeviceCaptureRetainsEndWhenEvidenceIsAddedLater()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var id = state.CreateDeviceSession(new WorkspaceTestTarget("ios", "simulator-001", "Phone",
            "test.duration", false, false, true) { DeviceKind = "simulator", ExecutionMode = "device" });

        state.EndDeviceSession(id);
        Assert.True(state.TryGetSessionSnapshot(id, out var completed));
        var endedUtc = completed!.LastUpdatedUtc;
        Assert.Equal(endedUtc, completed.CustomProperties!["captureEndedUtc"]!.GetValue<DateTimeOffset>());

        state.AddSessionApplicationEvents(id, [new SessionApplicationEvent(
            Guid.NewGuid().ToString("N"), "metrics recovered", "host.instruments.metrics.ingested",
            "", endedUtc.AddHours(2), 0)]);

        Assert.True(state.TryGetSessionSnapshot(id, out var enriched));
        Assert.Equal(endedUtc, enriched!.LastUpdatedUtc);
        Assert.Equal(endedUtc, state.GetSessionSummaries().Single(summary => summary.SessionId == id).LastUpdatedUtc);
    }
}
