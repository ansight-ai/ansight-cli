using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class LocalSessionSummaryRunnerTests
{
    [Fact]
    public void FormatSummary_RendersDescriptionAndChronologicalSteps()
    {
        const string response = """
            {"sessionDescription":"In this session, the tester opened the map and reached a failed search.","steps":["Opened the Map tab","Searched for \"Bunny Bucket\"","Saw the search error"]}
            """;

        var summary = LocalSessionSummaryRunner.FormatSummary(response);

        Assert.Equal("In this session, the tester opened the map and reached a failed search.\n\n"
            + "1. Opened the Map tab\n2. Searched for \"Bunny Bucket\"\n3. Saw the search error", summary);
    }

    [Fact]
    public void FormatSummary_RejectsMissingSteps()
    {
        const string response = """
            {"sessionDescription":"In this session, the tester opened the map.","steps":[]}
            """;

        Assert.Throws<InvalidOperationException>(() => LocalSessionSummaryRunner.FormatSummary(response));
    }

    [Fact]
    public void SelectSection_BoundsEveryEvidenceSourceWithoutChangingTheSession()
    {
        var snapshot = CreateSnapshot();
        var section = LocalSessionSummaryRunner.SelectSection(snapshot, At(10), At(20));

        Assert.Equal(At(10), section.CreatedUtc);
        Assert.Equal(At(20), section.LastUpdatedUtc);
        Assert.Equal(snapshot.SessionId, section.SessionId);
        Assert.Equal(snapshot.AppId, section.AppId);
        Assert.Equal(new[] { At(10), At(15), At(20) }, section.Images.Select(item => item.CapturedAtUtc));
        Assert.Equal(new[] { At(10), At(15), At(20) }, section.Logs.Select(item => item.TimestampUtc));
        Assert.Equal(new[] { At(10), At(15), At(20) }, section.Touches.Select(item => item.CapturedAtUtc));
        Assert.Equal(new[] { At(10), At(15), At(20) }, section.ApplicationEvents.Select(item => item.CapturedAtUtc));
        Assert.Equal(new[] { At(10), At(15), At(20) }, section.VisualTreeSnapshots.Select(item => item.CapturedAtUtc));
        Assert.Equal(new[] { At(10), At(15), At(20) }, section.Metrics.Select(item => item.CapturedAtUtc));
        Assert.Equal(new[] { At(10), At(15) }, section.NetworkRequests.Select(item => item.StartedAtUtc));
        Assert.Equal(new[] { "inside", "point" }, section.Annotations.Select(item => item.AnnotationId));
        Assert.Single(section.MetricChannels);
        Assert.Empty(section.Analyses);
        Assert.Null(section.Notes);
        Assert.Equal(5, snapshot.Images.Count);
        Assert.Equal(5, snapshot.Annotations.Count);

        var evidence = LocalSessionSummaryRunner.BuildEvidence(section);
        Assert.Contains("inside", evidence);
        Assert.DoesNotContain("outside", evidence);
    }

    [Theory]
    [InlineData(20, 10)]
    [InlineData(10, 10)]
    [InlineData(40, 50)]
    public void SelectSection_RejectsInvalidOrEmptyRanges(int startSeconds, int endSeconds)
    {
        Assert.Throws<ArgumentException>(() => LocalSessionSummaryRunner.SelectSection(CreateSnapshot(), At(startSeconds), At(endSeconds)));
    }

    [Fact]
    public void SelectSection_RejectsMissingTimestamps()
    {
        Assert.Throws<ArgumentException>(() => LocalSessionSummaryRunner.SelectSection(CreateSnapshot(), default, At(20)));
        Assert.Throws<ArgumentException>(() => LocalSessionSummaryRunner.SelectSection(CreateSnapshot(), At(10), default));
    }

    private static DateTimeOffset At(int seconds) => DateTimeOffset.Parse("2026-10-08T02:00:00Z").AddSeconds(seconds);

    private static AppSessionSnapshot CreateSnapshot()
    {
        var seconds = new[] { 0, 10, 15, 20, 30 };
        string Label(int second) => second >= 10 && second <= 20 ? "inside" : "outside";
        return new AppSessionSnapshot
        {
            SessionId = "section-summary",
            AppId = "com.example.app",
            ClientName = "Test app",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = At(0),
            LastUpdatedUtc = At(30),
            ConfigId = null,
            Status = "Closed",
            IsHistorical = true,
            Notes = "outside session notes",
            Logs = seconds.Select(second => new LogEntry(At(second), Label(second))).ToArray(),
            Images = seconds.Select(second => new SessionImageFrame
            {
                FrameId = $"frame-{second}", CapturedAtUtc = At(second), Format = "png", Width = 1, Height = 1, Quality = 100, ByteCount = 1
            }).ToArray(),
            Touches = seconds.Select(second => new SessionTouchInputRecord
            {
                Id = $"touch-{second}", CapturedAtUtc = At(second), Action = Label(second), PointerId = 0, PointerIndex = 0,
                PointerCount = 1, X = 1, Y = 1, CoordinateUnit = "px"
            }).ToArray(),
            NetworkRequests = seconds.Select(second => new SessionNetworkRequest
            {
                Id = $"network-{second}", Source = "test", StartedAtUtc = At(second), CompletedAtUtc = At(second + 1),
                DurationMilliseconds = 1000, Method = "GET", Url = $"https://example.com/{Label(second)}"
            }).ToArray(),
            ApplicationEvents = seconds.Select(second => new SessionApplicationEvent($"event-{second}", Label(second), "test", "", At(second), 1)).ToArray(),
            VisualTreeSnapshots = seconds.Select(second => new SessionVisualTreeSnapshot
            {
                SnapshotId = $"tree-{second}", CapturedAtUtc = At(second), Source = Label(second), NodeCount = 0
            }).ToArray(),
            Metrics = seconds.Select(second => new SessionMetricSample { ChannelId = 1, CapturedAtUtc = At(second), Value = second }).ToArray(),
            MetricChannels =
            [
                new SessionMetricChannel { ChannelId = 1, Name = "FPS", ColorHex = "#00ff00" },
                new SessionMetricChannel { ChannelId = 2, Name = "outside channel", ColorHex = "#ff0000" }
            ],
            Annotations =
            [
                new SessionAnnotation { AnnotationId = "before", StartUtc = At(0), EndUtc = At(5), Label = "outside" },
                new SessionAnnotation { AnnotationId = "overlap-start", StartUtc = At(5), EndUtc = At(15), Label = "outside" },
                new SessionAnnotation { AnnotationId = "inside", StartUtc = At(10), EndUtc = At(20), Label = "inside" },
                new SessionAnnotation { AnnotationId = "point", StartUtc = At(15), Label = "inside" },
                new SessionAnnotation { AnnotationId = "overlap-end", StartUtc = At(15), EndUtc = At(25), Label = "outside" }
            ]
        };
    }
}
