using Ansight.Host.Connectivity;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class ConnectivityTests
{
    private static readonly DateTimeOffset origin = DateTimeOffset.Parse("2026-10-10T00:00:00Z");
    private static ConnectivityRecord Record(string id, int second, string availability = "available", string kind = "snapshot") => new()
    {
        Id = id, ObservationId = "test", Sequence = second, ElapsedMilliseconds = second * 1000,
        CapturedAtUtc = origin.AddSeconds(second), Source = "test", Kind = kind,
        Snapshot = new() { Availability = availability, Transports = availability == "available" ? ["wifi"] : [] }
    };
    private static AppSessionSnapshot Session(params ConnectivityRecord[] records) => new()
    {
        SessionId = "connectivity-test", AppId = "test", ClientName = "test", RemoteAddress = "127.0.0.1", ConfigId = null,
        CreatedUtc = origin, LastUpdatedUtc = origin.AddSeconds(100), Status = "Historical", IsHistorical = true,
        MetricChannels = [], Metrics = [], ApplicationEvents = records.Select(SessionConnectivity.ToEvent).ToArray()
    };

    [Theory]
    [InlineData("summary")]
    [InlineData("timeline")]
    [InlineData("at")]
    public void AgentToolsHaveRepositoryTaskApiMappings(string method)
    {
        var mapping = Ansight.Host.Runtime.RepositoryContracts.RepositoryJavaScriptApiMethods.ResolveHostApiMethod($"ansight_get_connectivity_{method}");
        Assert.Equal("connectivity", mapping.FeatureName);
        Assert.Equal(method, mapping.MethodName);
    }

    [Fact]
    public void SummaryDistinguishesNoObservationFromDisconnectedAndClipsToCapture()
    {
        var first = Record("first", 10);
        var offline = Record("offline", 30, "unavailable", "change") with { PreviousSnapshot = first.Snapshot };
        var gap = Record("gap", 50, "unknown", "gap");
        var session = Session(first, offline, gap, Record("resume", 80));
        var summary = SessionConnectivity.Query(session, from: origin.AddSeconds(-10), to: origin.AddSeconds(200));
        var durations = summary["availabilityDurationMilliseconds"]!;
        Assert.Equal(40000, durations["available"]!.GetValue<double>());
        Assert.Equal(20000, durations["unavailable"]!.GetValue<double>());
        Assert.Equal(40000, durations["unknown"]!.GetValue<double>());
        Assert.True(summary["rangeClipped"]!.GetValue<bool>());
        Assert.Equal(1, summary["disconnectionCount"]!.GetValue<int>());
        Assert.Equal("unknown", SessionConnectivity.Query(session, "at", at: origin.AddSeconds(101))["availability"]!.GetValue<string>());
        Assert.Null(SessionConnectivity.Query(session, "at", at: origin.AddSeconds(5))["record"]);
    }

    [Fact]
    public void TimelinePagesDeduplicateCheckpointsAndRejectChangedFilters()
    {
        var records = Enumerable.Range(0, 100).Select(index => Record(index.ToString(), index)).ToArray();
        var session = Session(records.Concat([records[0]]).ToArray());
        var first = SessionConnectivity.Query(session, "timeline", limit: 9);
        Assert.Equal(100, first["recordCount"]!.GetValue<int>());
        var cursor = first["nextCursor"]!.GetValue<string>();
        var next = SessionConnectivity.Query(session, "timeline", limit: 9, cursor: cursor);
        Assert.Equal("9", next["records"]![0]!["id"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => SessionConnectivity.Query(session, "quality", cursor: cursor));
        Assert.Throws<ArgumentException>(() => SessionConnectivity.Query(session, limit: 1001));
    }

    [Fact]
    public void ExtractAndShiftPreserveSourceTimeAndSampleAge()
    {
        var baseline = Record("baseline", 10) with { Quality = new() { Source = "test", MeasuredAtUtc = origin.AddSeconds(8), SignalStrength = -70, SignalStrengthUnit = "dBm" } };
        var records = Session(baseline, Record("later", 40));
        var extracted = SessionConnectivity.Extract(records.ApplicationEvents, origin.AddSeconds(20), origin.AddSeconds(50));
        Assert.Equal("baseline", extracted[0].EventId);
        Assert.Equal(origin.AddSeconds(10), extracted[0].CapturedAtUtc);
        var shifted = ConnectivityJson.Parse(SessionConnectivity.ShiftEvent(extracted[0], origin).Details)!;
        Assert.Equal(origin.AddSeconds(10), shifted.OriginalCapturedAtUtc);
        Assert.Equal(origin.AddSeconds(8), shifted.Quality!.OriginalMeasuredAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(2), shifted.CapturedAtUtc - shifted.Quality.MeasuredAtUtc);
    }

    [Theory]
    [InlineData(20, 60, "unavailable", 50)]
    [InlineData(20, 100, "available", 10)]
    public void CutPreservesStateAtTheJoinAndNeverAppendsBeyondTheCapture(int from, int to, string expectedAvailability, int originalSecond)
    {
        using var environment = new TestSupport.TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var imported = state.ImportSessionSnapshot(Session(Record("wifi", 10), Record("offline", 50, "unavailable")), new Dictionary<string, byte[]>());
        Assert.True(imported.IsSuccess);
        var id = imported.ImportedSession!.SessionId;
        Assert.True(state.TrimSessionTimeline(id, origin.AddSeconds(from), origin.AddSeconds(to), SessionTimelineTrimMode.CutSelection).IsSuccess);
        Assert.True(state.TryGetSessionSnapshot(id, out var snapshot));
        var records = SessionConnectivity.Read(snapshot!.ApplicationEvents);
        Assert.All(records, value => Assert.True(value.CapturedAtUtc <= snapshot.LastUpdatedUtc));
        Assert.Equal(expectedAvailability, records[^1].Snapshot.Availability);
        Assert.Equal(origin.AddSeconds(originalSecond), records[^1].OriginalCapturedAtUtc ?? records[^1].CapturedAtUtc);
    }

    [Fact]
    public void RawAndStreamableArchivesImportCanonicalSegmentsWithoutCompatibilityEvents()
    {
        var baseline = Record("baseline", 10);
        var gap = Record("gap", 50, "unknown", "gap");
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
            SessionConnectivityArchive.WriteZip(zip, [baseline, gap]);
        stream.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        Assert.True(SessionArchiveExternalPayloadReader.TryReadJsonLinesArchive(archive, "offline.ansight", out var imported, out var error), error);
        Assert.Equal(2, SessionConnectivity.Read(imported!.Snapshot.ApplicationEvents).Count);
        var streamed = SessionArchiveExternalPayloadReader.ApplyExternalArchivePayloads(archive, Session());
        Assert.Equal(new[] { "baseline", "gap" }, SessionConnectivity.Read(streamed.ApplicationEvents).Select(value => value.Id));
    }

    [Fact]
    public void ArchiveSegmentsRoundTripAndRewriteRemovesStaleSegments()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansight-connectivity-" + Guid.NewGuid());
        try
        {
            var records = Enumerable.Range(0, 600).Select(index => Record(index.ToString(), index)).ToArray();
            SessionConnectivityArchive.WriteDirectory(root, records);
            var loaded = SessionConnectivity.Read(SessionConnectivityArchive.ReadDirectory(root));
            Assert.Equal(600, loaded.Count);
            Assert.Equal(records.Select(record => record.Id), loaded.Select(record => record.Id));
            SessionConnectivityArchive.WriteDirectory(root, records.Take(5).ToArray());
            Assert.Single(Directory.GetFiles(Path.Combine(root, "connectivity", "segments")));
            Assert.Equal(5, SessionConnectivity.Read(SessionConnectivityArchive.ReadDirectory(root)).Count);
        }
        finally { Directory.Delete(root, true); }
    }
}
