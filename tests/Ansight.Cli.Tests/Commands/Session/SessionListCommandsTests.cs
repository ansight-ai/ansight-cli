using Ansight.Cli.Commands.Session;
using Ansight.Host;
using Ansight.Pairing.Models;

namespace Ansight.Cli.Tests.Commands.Session;

public sealed class SessionListCommandsTests
{
    [Fact]
    public async Task RunAsync_HelpDocumentsBoundedCursorPagination()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await SessionCommands.RunAsync(
            CliArguments.Parse(["session", "help"]),
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Page size from 1 to 1000; default: 20", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("--cursor <cursor>", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public void QuerySessionSummaries_DefaultsToTwentyAndPaginatesWithoutDuplicates()
    {
        var now = DateTimeOffset.Parse("2026-08-30T04:00:00Z");
        var summaries = Enumerable.Range(0, 25)
            .Select(index => CreateSummary(
                $"session-{index:D2}",
                "com.example.app",
                now.AddMinutes(-index)))
            .ToArray();

        var firstPage = SessionCommands.QuerySessionSummaries(
            summaries,
            CliArguments.Parse(["session", "list"]),
            static _ => false);

        Assert.Equal(25, firstPage.MatchedCount);
        Assert.Equal(20, firstPage.Limit);
        Assert.Equal(20, firstPage.Items.Count);
        Assert.True(firstPage.IsTruncated);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = SessionCommands.QuerySessionSummaries(
            summaries,
            CliArguments.Parse(
                ["session", "list", "--cursor", firstPage.NextCursor!]),
            static _ => false);

        Assert.Equal(25, secondPage.MatchedCount);
        Assert.Equal(5, secondPage.Items.Count);
        Assert.False(secondPage.IsTruncated);
        Assert.Null(secondPage.NextCursor);
        Assert.Empty(firstPage.Items.Select(static item => item.Session.SessionId)
            .Intersect(secondPage.Items.Select(static item => item.Session.SessionId), StringComparer.Ordinal));
    }

    [Fact]
    public void QuerySessionSummaries_AppliesIdentityTimePlatformAndEvidenceFiltersToSummaries()
    {
        var now = DateTimeOffset.Parse("2026-08-30T04:00:00Z");
        var matching = CreateSummary(
            "matching",
            "com.alphaoutdoors.redpoint",
            now.AddMinutes(-30),
            createdUtc: now.AddHours(-2),
            platformOsName: "iOS",
            formFactor: "Phone",
            isVirtual: true,
            tags: ["qa", "map"],
            totalLogCount: 12,
            totalMetricSampleCount: 8);
        var wrongApp = CreateSummary(
            "wrong-app",
            "com.example.other",
            now.AddMinutes(-30),
            createdUtc: now.AddHours(-2),
            platformOsName: "iOS",
            tags: ["qa"],
            totalLogCount: 12,
            totalMetricSampleCount: 8);
        var outsideRange = CreateSummary(
            "outside-range",
            "com.alphaoutdoors.redpoint",
            now.AddHours(-3),
            createdUtc: now.AddHours(-4),
            platformOsName: "iOS",
            tags: ["qa"],
            totalLogCount: 12,
            totalMetricSampleCount: 8);
        var wrongPlatform = CreateSummary(
            "wrong-platform",
            "com.alphaoutdoors.redpoint",
            now.AddMinutes(-30),
            createdUtc: now.AddHours(-2),
            platformOsName: "Android",
            tags: ["qa"],
            totalLogCount: 12,
            totalMetricSampleCount: 8);

        var page = SessionCommands.QuerySessionSummaries(
            [matching, wrongApp, outsideRange, wrongPlatform],
            CliArguments.Parse(
            [
                "session", "list",
                "--app-id", "com.alphaoutdoors.redpoint",
                "--from", now.AddHours(-1).ToString("O"),
                "--to", now.ToString("O"),
                "--platform", "ios",
                "--device-form-factor", "Phone",
                "--virtual",
                "--tag", "qa",
                "--has-logs",
                "--has-telemetry"
            ]),
            static _ => false);

        var item = Assert.Single(page.Items);
        Assert.Same(matching, item.Session);
        Assert.Empty(matching.Logs);
        Assert.Empty(matching.Metrics);
    }

    [Fact]
    public void QuerySessionSummaries_ConnectedFilterUsesConnectionState()
    {
        var now = DateTimeOffset.Parse("2026-08-30T04:00:00Z");
        var page = SessionCommands.QuerySessionSummaries(
            [
                CreateSummary("historical", "com.example.app", now),
                CreateSummary("live", "com.example.app", now.AddMinutes(-1))
            ],
            CliArguments.Parse(["session", "list", "--connected"]),
            static sessionId => sessionId == "live");

        var item = Assert.Single(page.Items);
        Assert.Equal("live", item.Session.SessionId);
        Assert.True(item.IsConnected);
    }

    [Fact]
    public void QuerySessionSummaries_UsesSessionIdToPaginateEqualTimestamps()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-30T04:00:00Z");
        var summaries = new[]
        {
            CreateSummary("charlie", "com.example.app", timestamp),
            CreateSummary("alpha", "com.example.app", timestamp),
            CreateSummary("bravo", "com.example.app", timestamp)
        };
        var firstPage = SessionCommands.QuerySessionSummaries(
            summaries,
            CliArguments.Parse(["session", "list", "--limit", "2"]),
            static _ => false);

        Assert.Equal(
            ["alpha", "bravo"],
            firstPage.Items.Select(static item => item.Session.SessionId));

        var secondPage = SessionCommands.QuerySessionSummaries(
            summaries,
            CliArguments.Parse(
                ["session", "list", "--limit", "2", "--cursor", firstPage.NextCursor!]),
            static _ => false);

        Assert.Equal("charlie", Assert.Single(secondPage.Items).Session.SessionId);
    }

    [Fact]
    public void QuerySessionSummaries_RejectsAnInvalidCursor()
    {
        var exception = Assert.Throws<CliUsageException>(() =>
            SessionCommands.QuerySessionSummaries(
                [],
                CliArguments.Parse(["session", "list", "--cursor", "invalid"]),
                static _ => false));

        Assert.Equal("--cursor is not a valid session-list cursor.", exception.Message);
    }

    [Fact]
    public void QuerySessionSummaries_RejectsAnInvertedTimeRange()
    {
        var exception = Assert.Throws<CliUsageException>(() =>
            SessionCommands.QuerySessionSummaries(
                [],
                CliArguments.Parse(
                [
                    "session", "list",
                    "--from", "2026-08-30T05:00:00Z",
                    "--to", "2026-08-30T04:00:00Z"
                ]),
                static _ => false));

        Assert.Equal("--from must be earlier than or equal to --to.", exception.Message);
    }

    private static AppSessionSnapshot CreateSummary(
        string sessionId,
        string appId,
        DateTimeOffset lastUpdatedUtc,
        DateTimeOffset? createdUtc = null,
        string platformOsName = "iOS",
        string formFactor = "Phone",
        bool isVirtual = false,
        IReadOnlyList<string>? tags = null,
        int totalLogCount = 0,
        int totalMetricSampleCount = 0)
    {
        return new AppSessionSnapshot
        {
            SessionId = sessionId,
            AppId = appId,
            ClientName = "Session list test",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc ?? lastUpdatedUtc.AddMinutes(-5),
            ConfigId = null,
            Status = "WebSocket Closed",
            LastUpdatedUtc = lastUpdatedUtc,
            IsHistorical = true,
            Tags = tags ?? [],
            DeviceProfile = new DeviceAppProfile
            {
                Device = new DeviceProfile
                {
                    Model = "Test device",
                    FormFactor = formFactor,
                    OsName = platformOsName,
                    OsVersion = "18.0",
                    IsVirtual = isVirtual,
                    IsEmulator = isVirtual
                },
                App = new DeviceApplicationProfile
                {
                    AppId = appId,
                    AppName = appId,
                    VersionName = "1.0"
                }
            },
            TotalLogCount = totalLogCount,
            MetricChannels = [],
            Metrics = [],
            TotalMetricSampleCount = totalMetricSampleCount
        };
    }
}
