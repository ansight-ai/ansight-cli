using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Trends;

public sealed class WorkspaceTrendsLogTests
{
    [Theory]
    [InlineData(SessionLogStreamIds.AndroidLogcat, "android", DeviceKinds.Emulator)]
    [InlineData(SessionLogStreamIds.AppleUnifiedLog, "ios", DeviceKinds.Simulator)]
    public async Task RegisteredExternalSessionEvaluatesTelemetryBetweenNativeLogs(string streamId, string platform, string kind)
    {
        using var environment = new TestEnvironment();
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        using var runtime = composition.Get<RuntimeCoordinator>();
        var state = composition.Get<IRuntimeState>();
        var directory = Path.Combine(environment.RootPath, "ansight", "trends");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "native.json"), Definition(streamId));
        Assert.True(runtime.Apps.Register(new AppRegistrationRequest("test.notes", "Notes", environment.RootPath)).IsSuccess);
        var id = state.CreateDeviceSession(new WorkspaceTestTarget(platform, "sim", "Phone", "test.notes", false, false, false)
        { DeviceKind = kind, ExecutionMode = "device" });
        var start = DateTimeOffset.UtcNow;
        state.AddSessionLogEntries(id, streamId, [
            new LogEntry(start, "Loading notes") { Tag = "Notes", ProcessId = 42, EventId = "begin" },
            new LogEntry(start.AddSeconds(2), "Loaded notes: 52") { Tag = "Notes", ProcessId = 42, EventId = "end" }
        ]);
        state.UpdateSessionMetricChannels(id, [new SessionMetricChannel
        { ChannelId = 10, Name = "Process CPU", ColorHex = "#00ff00", Type = "cpu", Unit = "millicores" }]);
        state.AddSessionMetrics(id, [
            new SessionMetricSample { ChannelId = 10, Value = 100, CapturedAtUtc = start },
            new SessionMetricSample { ChannelId = 10, Value = 300, CapturedAtUtc = start.AddSeconds(1) },
            new SessionMetricSample { ChannelId = 10, Value = 200, CapturedAtUtc = start.AddSeconds(2) }
        ], telemetrySegmentId: 0);
        state.EndDeviceSession(id);
        var report = await runtime.Trends.EvaluateRegisteredSessionAsync(id);
        Assert.NotNull(report);
        Assert.Equal(WorkspaceTrendsStatus.Passed, report.Status);
        var check = Assert.Single(report.Checks);
        Assert.Equal(200d, Assert.Single(check.Metrics).Value);
        Assert.Equal($"log:{streamId}:begin", Assert.Single(check.SpanInstances).StartEventId);
    }

    [Theory]
    [InlineData(SessionLogStreamIds.AndroidLogcat)]
    [InlineData(SessionLogStreamIds.AppleUnifiedLog)]
    public void LogAnchorsFilterAndPairRepeatedIntervalsWithoutSdkEvents(string streamId)
    {
        var start = DateTimeOffset.UtcNow;
        var definition = Parse(Definition(streamId));
        var entries = new[]
        {
            Entry("Loading notes", 0, "wrong-tag", 42),
            Entry("Loaded notes: 52", 1, "Notes", 42),
            Entry("Loading notes", 2, "Notes", 99),
            Entry("Loaded notes: 52", 3, "Notes", 99),
            Entry("Loading notes", 4, "Notes", 42),
            Entry("Loaded notes: 52", 5, "Notes", 42),
            Entry("Loading notes", 6, "Notes", 42),
            Entry("Loaded notes: 53", 7, "Notes", 42)
        };
        var snapshot = Snapshot(start,
            [new SessionLogStream { StreamId = streamId, Kind = streamId, DisplayName = streamId, Entries = entries }],
            // The flat projection must not count these entries a second time.
            entries.Select(entry => entry with { StreamId = streamId }).ToArray());
        var result = WorkspaceTrendsSpanResolver.Resolve(snapshot, definition.Span);
        Assert.Equal(WorkspaceTrendsStatus.Passed, result.Status);
        Assert.Equal(2, result.Instances.Count);
        Assert.Equal(start.AddSeconds(4), result.Instances[0].StartUtc);
        Assert.Equal(start.AddSeconds(7), result.Instances[1].EndUtc);
        Assert.All(result.Instances, value => Assert.Null(value.Group));
        var wrongStream = Snapshot(start, [], entries);
        Assert.Equal(WorkspaceTrendsStatus.Inconclusive, WorkspaceTrendsSpanResolver.Resolve(wrongStream, definition.Span).Status);
        var legacyOnly = Snapshot(start, [], snapshot.Logs);
        Assert.Equal(result.Instances, WorkspaceTrendsSpanResolver.Resolve(legacyOnly, definition.Span).Instances);

        LogEntry Entry(string message, int second, string tag, int pid) => new(start.AddSeconds(second), message)
        { Tag = tag, ProcessId = pid, EventId = second.ToString() };
    }

    [Theory]
    [InlineData("\"match\":\"regex\",")]
    [InlineData("\"priority\":\"Bogus\",")]
    [InlineData("\"processId\":0,")]
    [InlineData("\"typo\":true,")]
    public void InvalidLogSelectorsAreRefused(string property)
    {
        var source = Definition("android-logcat").Replace(", \"processId\": 42", "")
            .Replace("\"streamId\":", property + "\"streamId\":");
        Assert.Throws<InvalidDataException>(() => Parse(source));
    }

    [Fact]
    public void AnchorCannotSelectBothAnEventAndALog()
    {
        var source = Definition("android-logcat").Replace("\"start\": {", "\"start\": { \"event\": { \"label\": \"loading\" },");
        Assert.Throws<InvalidDataException>(() => Parse(source));
    }

    [Fact]
    public void EventAnchorSerializationKeepsExistingHistoryHashesStable()
    {
        Assert.Equal("{\"Label\":\"start\",\"EventType\":null,\"ChannelId\":null}",
            JsonSerializer.Serialize(new WorkspaceEventAnchor("start")));
    }

    private static WorkspaceTrendsDefinition Parse(string source)
        => WorkspaceTrendsCatalog.ParseTrends("/tmp", "/tmp/native.json", source);

    private static string Definition(string streamId) => $$"""
        {
          "schemaVersion": 1, "id": "native", "appId": "test.notes",
          "span": {
            "start": { "log": { "streamId": "{{streamId}}", "message": "Loading notes", "tag": "Notes", "processId": 42 } },
            "end": { "log": { "streamId": "{{streamId}}", "message": "Loaded notes:", "match": "contains", "tag": "Notes", "processId": 42 } },
            "selection": "all", "maximumDurationMs": 10000
          },
          "metrics": [{ "id": "cpu", "channel": { "name": "Process CPU" }, "statistic": "average", "budget": { "lte": 500 } }]
        }
        """;

    private static AppSessionSnapshot Snapshot(DateTimeOffset start, IReadOnlyList<SessionLogStream> streams, IReadOnlyList<LogEntry> logs) => new()
    {
        SessionId = "session", AppId = "test.notes", ClientName = "Notes", RemoteAddress = "local",
        ConfigId = null, CreatedUtc = start, LastUpdatedUtc = start.AddSeconds(8), IsHistorical = false,
        Status = "Completed", CaptureSource = "device", MetricChannels = [], Metrics = [], LogStreams = streams, Logs = logs
    };
}
