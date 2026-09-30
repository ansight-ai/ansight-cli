namespace Ansight.Host.Tests.Unit.Analytics;

public sealed class LocalSessionCaptureAnalyticsTests
{
    [Theory]
    [InlineData(null, "WebSocket Closed", true)]
    [InlineData("Connected", "WebSocket Closed", true)]
    [InlineData("WebSocket Open", "WebSocket Complete", true)]
    [InlineData("WebSocket Closed", "WebSocket Closed", false)]
    [InlineData("Connected", "Sign In Required", false)]
    [InlineData("Awaiting WebSocket", "WebSocket Timeout", false)]
    public void ShouldReportCompletion_OnlyReportsFirstPersistedCaptureCompletion(
        string? previousStatus,
        string status,
        bool expected)
    {
        var snapshot = CreateSnapshot(status);

        var actual = LocalSessionCaptureAnalytics.ShouldReportCompletion(previousStatus, snapshot);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ShouldReportCompletion_DoesNotReportLiveSnapshot()
    {
        var snapshot = CreateSnapshot("WebSocket Closed", isHistorical: false);

        var actual = LocalSessionCaptureAnalytics.ShouldReportCompletion("WebSocket Open", snapshot);

        Assert.False(actual);
    }

    [Fact]
    public void CreateCompletionProperties_IncludesReportableCaptureSummary()
    {
        var snapshot = CreateSnapshot("WebSocket Closed");

        var properties = LocalSessionCaptureAnalytics.CreateCompletionProperties(snapshot);

        Assert.Equal("local", properties["captureScope"]);
        Assert.Equal("disconnected", properties["completionOutcome"]);
        Assert.Equal("session-001", properties["sessionId"]);
        Assert.Equal("com.example.app", properties["appId"]);
        Assert.Equal(15d, properties["durationSeconds"]);
        Assert.Equal(12, properties["logCount"]);
        Assert.Equal(3, properties["imageCount"]);
        Assert.Equal(20, properties["metricSampleCount"]);
        Assert.Equal(true, properties["hasEvidence"]);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("Awaiting WebSocket", false)]
    [InlineData("WebSocket Closed", false)]
    [InlineData("WebSocket Open", true)]
    [InlineData("Connected", true)]
    public void ActivationRequiresAnObservedLiveConnection(string? previous, bool expected)
        => Assert.Equal(expected, LocalSessionCaptureAnalytics.ShouldReportActivation(previous, CreateSnapshot("WebSocket Closed")));

    [Fact]
    public void EmptySessionDoesNotCountAsActivation()
        => Assert.False(LocalSessionCaptureAnalytics.ShouldReportActivation("WebSocket Open", CreateSnapshot("WebSocket Closed", hasEvidence: false)));

    [Theory]
    [InlineData("WebSocket Error", "failed")]
    [InlineData("WebSocket Timeout", "failed")]
    [InlineData("Sign In Required", "blocked")]
    [InlineData("WebSocket Closed", "succeeded")]
    public void SavedRecordingsCountAfterAnObservedConnectionWithTheirActualOutcome(string status, string outcome)
    {
        var snapshot = CreateSnapshot(status);
        Assert.True(LocalSessionCaptureAnalytics.ShouldReportRecordedCapture("WebSocket Open", snapshot));
        Assert.True(LocalSessionCaptureAnalytics.ShouldReportActivation("WebSocket Open", snapshot));
        Assert.Equal(outcome, LocalSessionCaptureAnalytics.CaptureUsageOutcome(snapshot));
        Assert.Equal(15d, LocalSessionCaptureAnalytics.CreateCompletionProperties(snapshot)["durationSeconds"]);
        foreach (var previous in new string?[] { null, "Awaiting WebSocket", status })
            Assert.False(LocalSessionCaptureAnalytics.ShouldReportRecordedCapture(previous, snapshot));
        Assert.False(LocalSessionCaptureAnalytics.ShouldReportRecordedCapture("WebSocket Open", CreateSnapshot(status, isHistorical: false)));
        Assert.False(LocalSessionCaptureAnalytics.ShouldReportActivation("WebSocket Open", CreateSnapshot(status, hasEvidence: false)));
    }

    private static AppSessionSnapshot CreateSnapshot(string status, bool isHistorical = true, bool hasEvidence = true)
    {
        var createdUtc = new DateTimeOffset(2026, 8, 6, 2, 0, 0, TimeSpan.Zero);
        return new AppSessionSnapshot
        {
            SessionId = "session-001",
            AppId = "com.example.app",
            ClientName = "Example App",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            ProcessSessionId = "process-001",
            Status = status,
            LastUpdatedUtc = createdUtc.AddSeconds(15),
            IsHistorical = isHistorical,
            SdkVersion = "1.2.0",
            TotalLogCount = hasEvidence ? 12 : 0,
            TotalAnnotationCount = hasEvidence ? 2 : 0,
            TotalImageCount = hasEvidence ? 3 : 0,
            TotalMetricChannelCount = 2,
            TotalMetricSampleCount = hasEvidence ? 20 : 0,
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        };
    }
}
