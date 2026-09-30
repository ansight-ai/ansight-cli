using Ansight.Host.Trends;
using Ansight.Pairing.Models;

namespace Ansight.Host.Tests.Unit.Trends;

public sealed class WorkspaceTrendsServiceTests
{
    [Fact]
    public void RebuildHistory_ReplaysCurrentRulesAndLimitsReplacementToSelectedVersion()
    {
        using var environment = new TestSupport.TestEnvironment();
        var workspacePath = CreateTrendsWorkspace(environment.RootPath, "com.example.app");
        using var runtime = environment.CreateRuntime();
        Assert.True(runtime.Apps.Register(new AppRegistrationRequest(
            "com.example.app",
            "Example",
            workspacePath)).IsSuccess);
        var store = new WorkspaceTrendsHistoryStore(environment.ApplicationPaths);
        var version1First = HistoricalContext("evaluation-v1-1", "com.example.app", "1.0", 1);
        var version1Second = HistoricalContext("evaluation-v1-2", "com.example.app", "1.0", 2);
        var version2First = HistoricalContext("evaluation-v2-1", "com.example.app", "2.0", 3);
        var version2Second = HistoricalContext("evaluation-v2-2", "com.example.app", "2.0", 4);
        store.SaveMetrics(version1First, [HistoricalCheck(100)]);
        store.SaveMetrics(version1Second, [HistoricalCheck(100)]);
        store.SaveMetrics(version2First, [HistoricalCheck(120)]);
        store.SaveMetrics(version2Second, [HistoricalCheck(120)]);
        store.SaveHistoryDecision(version1First, OldHistoryDecision("1.0"), isBreach: false);
        store.SaveHistoryDecision(version2First, OldHistoryDecision("2.0"), isBreach: false);

        var rebuilt = runtime.Trends.RebuildHistory(new WorkspaceTrendsHistoryRebuildRequest(
            AppId: "com.example.app",
            AppVersion: "2.0"));

        Assert.False(rebuilt.DryRun);
        Assert.Equal(1, rebuilt.RemovedHistoryResultCount);
        Assert.Equal(2, rebuilt.RebuiltHistoryResultCount);
        Assert.Empty(rebuilt.SkippedApps);
        var history = store.LoadHistory("com.example.app", null, 20);
        Assert.Contains(history, entry => entry.AppVersion == "1.0" && entry.DefinitionHash == "old-rule");
        var version2 = history
            .Where(static entry => entry.AppVersion == "2.0")
            .OrderBy(static entry => entry.EvaluatedAtUtc)
            .ToArray();
        Assert.Equal(2, version2.Length);
        Assert.Equal(WorkspaceTrendsHistoryStatus.Suspect, version2[0].Status);
        Assert.Equal(WorkspaceTrendsHistoryStatus.Regressed, version2[1].Status);
        Assert.All(version2, entry => Assert.Equal("1.0", entry.BaselineAppVersion));
        Assert.All(version2, entry => Assert.NotEqual("old-rule", entry.DefinitionHash));
    }

    [Fact]
    public void RebuildHistory_DryRunCalculatesAllAppsWithoutChangingStoredResults()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var store = new WorkspaceTrendsHistoryStore(environment.ApplicationPaths);
        foreach (var appId in new[] { "com.example.first", "com.example.second" })
        {
            var workspacePath = CreateTrendsWorkspace(environment.RootPath, appId);
            Assert.True(runtime.Apps.Register(new AppRegistrationRequest(
                appId,
                appId,
                workspacePath)).IsSuccess);
            var context = HistoricalContext($"evaluation-{appId}", appId, null, 1);
            store.SaveMetrics(context, [HistoricalCheck(100)]);
            store.SaveHistoryDecision(context, OldHistoryDecision(null), isBreach: false);
        }

        var rebuilt = runtime.Trends.RebuildHistory(new WorkspaceTrendsHistoryRebuildRequest(
            DryRun: true));

        Assert.True(rebuilt.DryRun);
        Assert.Equal(2, rebuilt.Apps.Count);
        Assert.Equal(2, rebuilt.RemovedHistoryResultCount);
        Assert.Equal(2, rebuilt.RebuiltHistoryResultCount);
        Assert.Equal(2, store.LoadHistory(null, null, 20).Count);
        Assert.All(store.LoadHistory(null, null, 20), entry => Assert.Equal("old-rule", entry.DefinitionHash));
    }

    [Fact]
    public void EvaluateHistory_LabelsAnUnconfirmedBreachAsSuspect()
    {
        var historyDefinition = HistoryDefinition(
            new WorkspaceTrendsHistoryRegressionDefinition(10, null, null, null, 2));

        var result = WorkspaceTrendsService.EvaluateHistory(
            historyDefinition,
            WorkspaceTrendsHistoryComparison.PreviousVersion,
            120,
            null,
            "2.0",
            new WorkspaceTrendsHistoryBaselineData(
                [100, 100],
                WorkspaceTrendsHistoryStatus.Healthy,
                0,
                "1.0"));

        Assert.Equal(WorkspaceTrendsHistoryStatus.Suspect, result.Status);
        Assert.Equal(1, result.ConsecutiveBreachCount);
        Assert.Contains("1 of 2", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateHistory_RequiresEveryConfiguredRegressionThreshold()
    {
        var historyDefinition = HistoryDefinition(
            new WorkspaceTrendsHistoryRegressionDefinition(10, null, 50, null, 1));

        var result = WorkspaceTrendsService.EvaluateHistory(
            historyDefinition,
            WorkspaceTrendsHistoryComparison.PreviousVersion,
            120,
            null,
            "2.0",
            new WorkspaceTrendsHistoryBaselineData([100, 100], null, 0, "1.0"));

        Assert.Equal(WorkspaceTrendsHistoryStatus.Healthy, result.Status);
        Assert.False(WorkspaceTrendsService.IsRegression(
            historyDefinition.Regression,
            result.AbsoluteDelta,
            result.RelativeDeltaPercent));
    }

    [Fact]
    public void CreateRunContext_ReadsAppVersionFromSessionProfile()
    {
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "session-1",
            AppId = "com.example.app",
            ClientName = "test-client",
            RemoteAddress = "local",
            CreatedUtc = DateTimeOffset.Parse("2026-08-20T01:00:00Z"),
            ConfigId = null,
            Status = "Connected",
            LastUpdatedUtc = DateTimeOffset.Parse("2026-08-20T01:01:00Z"),
            IsHistorical = false,
            MetricChannels = [],
            Metrics = [],
            DeviceProfile = new DeviceAppProfile
            {
                App = new DeviceApplicationProfile
                {
                    AppId = "com.example.app",
                    AppName = "Example",
                    VersionName = " 2.4-beta "
                }
            }
        };
        var context = WorkspaceTrendsService.CreateRunContext(snapshot);

        Assert.Equal("2.4-beta", context.AppVersion);
    }

    [Fact]
    public void FindMissingSeriesDimension_DoesNotMergeIncompleteProfiles()
    {
        var context = RunContext() with
        {
            Platform = "iOS",
            DeviceModel = null,
            OperatingSystemMajor = "18"
        };

        var missing = WorkspaceTrendsService.FindMissingSeriesDimension(
            context,
            ["platform", "deviceModel", "operatingSystemMajor"]);

        Assert.Equal("deviceModel", missing);
        Assert.Null(WorkspaceTrendsService.FindMissingSeriesDimension(context, []));
    }

    [Fact]
    public void CreateSeriesKey_UsesOnlyConfiguredDimensions()
    {
        var first = RunContext() with { BuildNumber = "200" };
        var second = RunContext() with { BuildNumber = "201" };

        Assert.Equal(
            WorkspaceTrendsService.CreateSeriesKey(first, ["platform", "deviceModel"]),
            WorkspaceTrendsService.CreateSeriesKey(second, ["deviceModel", "platform"]));
        Assert.NotEqual(
            WorkspaceTrendsService.CreateSeriesKey(first, ["buildNumber"]),
            WorkspaceTrendsService.CreateSeriesKey(second, ["buildNumber"]));
    }

    [Fact]
    public void ResolveReportStatus_BlockingSignalErrorsDoNotPass()
    {
        var decision = new WorkspaceTrendsHistoryDecision(
            "trends.metric",
            "trends.metric",
            WorkspaceTrendsHistoryComparison.PreviousVersion,
            WorkspaceTrendsHistoryStatus.Error,
            "Missing series metadata.",
            42,
            null,
            null,
            null,
            0,
            5,
            0,
            "history-hash")
        {
            Blocking = true
        };

        var status = WorkspaceTrendsService.ResolveReportStatus([], [decision]);

        Assert.Equal(WorkspaceTrendsStatus.Error, status);
    }

    private static WorkspaceTrendsHistoryDefinition HistoryDefinition(WorkspaceTrendsHistoryRegressionDefinition regression)
        => new(
            "trends.metric",
            "trends.metric",
            new WorkspaceTrendsHistoryBaselineDefinition("referenceMedian", 10, 2),
            regression,
            ["platform"],
            "history-hash",
            "/trends.json");

    private static WorkspaceTrendsRunContext RunContext()
        => new(
            "evaluation",
            "run",
            "session",
            "com.example.app",
            "2.0",
            "200",
            "iOS",
            "iPhone",
            "18",
            null,
            DateTimeOffset.Parse("2026-08-23T00:00:00Z"));

    private static string CreateTrendsWorkspace(string rootPath, string appId)
    {
        var workspacePath = Path.Combine(rootPath, appId.Replace('.', '-'));
        var trendsPath = Path.Combine(workspacePath, "ansight", "trends");
        Directory.CreateDirectory(trendsPath);
        File.WriteAllText(Path.Combine(trendsPath, "trends.json"), $$"""
            {
              "schemaVersion": 1,
              "id": "trends",
              "appId": "{{appId}}",
              "span": {
                "start": { "event": { "label": "operation.started" } },
                "end": { "event": { "label": "operation.completed" } }
              },
              "metrics": [{
                "id": "metric",
                "channel": { "type": "memory" },
                "statistic": "average",
                "budget": { "lte": 200 },
                "regression": {
                  "percent": 10,
                  "confirmRuns": 2,
                  "baselineRuns": 2,
                  "minimumBaselineRuns": 2,
                  "seriesBy": ["platform"]
                }
              }]
            }
            """);
        return workspacePath;
    }

    private static WorkspaceTrendsRunContext HistoricalContext(
        string evaluationId,
        string appId,
        string? appVersion,
        int minute)
        => new(
            evaluationId,
            $"run-{evaluationId}",
            $"session-{evaluationId}",
            appId,
            appVersion,
            "100",
            "iOS",
            "iPhone",
            "18",
            "Release",
            DateTimeOffset.Parse("2026-08-23T00:00:00Z").AddMinutes(minute));

    private static WorkspaceTrendsCheckResult HistoricalCheck(double value)
        => new(
            "trends",
            true,
            WorkspaceTrendsStatus.Passed,
            "passed",
            [],
            [new WorkspaceTrendsMetricResult(
                "trends",
                "metric",
                "trends.metric",
                0,
                WorkspaceTrendsStatus.Passed,
                "passed",
                value,
                "MiB",
                null,
                10,
                DateTimeOffset.Parse("2026-08-23T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-23T00:00:01Z"),
                "metric-rule")],
            "trends-rule");

    private static WorkspaceTrendsHistoryDecision OldHistoryDecision(string? appVersion)
        => new(
            "trends.metric",
            "trends.metric",
            string.IsNullOrWhiteSpace(appVersion)
                ? WorkspaceTrendsHistoryComparison.UnversionedReference
                : WorkspaceTrendsHistoryComparison.PreviousVersion,
            WorkspaceTrendsHistoryStatus.Healthy,
            "old result",
            100,
            100,
            0,
            0,
            2,
            2,
            0,
            "old-rule")
        {
            BaselineAppVersion = appVersion,
            MetricDefinitionHash = "metric-rule",
            Unit = "MiB",
            SeriesKey = "old-series"
        };
}
