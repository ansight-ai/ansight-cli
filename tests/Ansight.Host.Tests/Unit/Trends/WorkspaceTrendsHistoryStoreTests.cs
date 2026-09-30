using Ansight.Host.Trends;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.Tests.Unit.Trends;

public sealed class WorkspaceTrendsHistoryStoreTests
{
    [Fact]
    public void SaveAndLoad_UsesTrendsMetricAndHistoryContracts()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new WorkspaceTrendsHistoryStore(environment.ApplicationPaths);
        var context = Context("evaluation-1", "session-1", 1);
        store.SaveMetrics(context, [Check(21)]);
        store.SaveHistoryDecision(context, Decision("Secret Garden", 21), isBreach: false);

        var metric = Assert.Single(store.LoadMetricHistory(
            "com.example.app",
            "load.metric",
            10,
            "Secret Garden"));
        var history = Assert.Single(store.LoadHistory(
            "com.example.app",
            "load.metric",
            10,
            "Secret Garden"));

        Assert.Equal("load", metric.TrendsId);
        Assert.Equal("metric", metric.MetricId);
        Assert.Equal("Secret Garden", metric.SpanGroup);
        Assert.Equal(21, history.CurrentValue);
        Assert.Equal("Secret Garden", history.SpanGroup);
        Assert.Contains("trends-history.sqlite3", store.DatabasePath, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadBaseline_UsesComparableRunsAndCohort()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new WorkspaceTrendsHistoryStore(environment.ApplicationPaths);
        store.SaveMetrics(Context("evaluation-1", "session-1", 1), [Check(10)]);
        store.SaveMetrics(Context("evaluation-2", "session-2", 2), [Check(20)]);

        var baseline = store.LoadBaseline(
            Context("evaluation-current", "session-4", 4),
            Metric(30),
            Definition(),
            WorkspaceTrendsHistoryComparison.UnversionedReference);

        Assert.Equal([10d, 20d], baseline.Values);
        Assert.Equal("1.0", baseline.BaselineAppVersion);
    }

    [Fact]
    public void DeleteSession_RemovesOnlyThatSessionsTrendsRows()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new WorkspaceTrendsHistoryStore(environment.ApplicationPaths);
        var first = Context("evaluation-1", "session-1", 1);
        var second = Context("evaluation-2", "session-2", 2);
        store.SaveMetrics(first, [Check(10)]);
        store.SaveMetrics(second, [Check(20)]);
        store.SaveHistoryDecision(first, Decision(null, 10), isBreach: false);
        store.SaveHistoryDecision(second, Decision(null, 20), isBreach: false);

        var removedRows = store.DeleteSession("session-1");

        Assert.Equal(2, removedRows);
        Assert.Equal("session-2", Assert.Single(store.LoadMetricHistory(null, null, 10)).SessionId);
        Assert.Equal("session-2", Assert.Single(store.LoadHistory(null, null, 10)).SessionId);
    }

    [Fact]
    public void SaveMetrics_DoesNotPersistWorkspaceTestCoupling()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new WorkspaceTrendsHistoryStore(environment.ApplicationPaths);
        store.SaveMetrics(Context("evaluation-1", "session-1", 1), [Check(10)]);

        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(trends_metrics);";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.DoesNotContain("test_id", columns);
        Assert.DoesNotContain("functional_succeeded", columns);
        Assert.Contains("span_group", columns);
        Assert.DoesNotContain("window_group", columns);
    }

    [Fact]
    public void LoadMetricHistory_RenamesLegacyGroupColumnWithoutLosingData()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new WorkspaceTrendsHistoryStore(environment.ApplicationPaths);
        store.SaveMetrics(Context("evaluation-1", "session-1", 1), [Check(10)]);

        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE trends_metrics RENAME COLUMN span_group TO window_group;
                ALTER TABLE trends_history RENAME COLUMN span_group TO window_group;
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }

        var metric = Assert.Single(store.LoadMetricHistory(
            "com.example.app",
            "load.metric",
            10,
            "Secret Garden"));

        Assert.Equal("Secret Garden", metric.SpanGroup);
        using var migratedConnection = new SqliteConnection($"Data Source={store.DatabasePath}");
        migratedConnection.Open();
        using var versionCommand = migratedConnection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(3L, versionCommand.ExecuteScalar());
    }

    private static WorkspaceTrendsRunContext Context(
        string evaluationId,
        string sessionId,
        int minute)
        => new(
            evaluationId,
            $"run-{minute}",
            sessionId,
            "com.example.app",
            "1.0",
            "100",
            "iOS",
            "iPhone",
            "18",
            "Release",
            DateTimeOffset.Parse("2026-08-20T01:00:00Z").AddMinutes(minute));

    private static WorkspaceTrendsCheckResult Check(double value)
        => new(
            "load",
            true,
            WorkspaceTrendsStatus.Passed,
            "passed",
            [],
            [Metric(value) with { SpanGroup = "Secret Garden" }],
            "trends-hash");

    private static WorkspaceTrendsMetricResult Metric(double value)
        => new(
            "load",
            "metric",
            "load.metric",
            0,
            WorkspaceTrendsStatus.Passed,
            "passed",
            value,
            "MiB",
            null,
            10,
            DateTimeOffset.Parse("2026-08-20T01:00:00Z"),
            DateTimeOffset.Parse("2026-08-20T01:00:05Z"),
            "metric-hash")
        {
            SpanGroup = "Secret Garden"
        };

    private static WorkspaceTrendsHistoryDefinition Definition()
        => new(
            "load.metric",
            "load.metric",
            new WorkspaceTrendsHistoryBaselineDefinition("referenceMedian", 10, 2),
            new WorkspaceTrendsHistoryRegressionDefinition(10, null, null, null, 1),
            ["platform"],
            "history-hash",
            "/trends.json");

    private static WorkspaceTrendsHistoryDecision Decision(string? spanGroup, double currentValue)
        => new(
            "load.metric",
            "load.metric",
            WorkspaceTrendsHistoryComparison.UnversionedReference,
            WorkspaceTrendsHistoryStatus.Healthy,
            "healthy",
            currentValue,
            10,
            currentValue - 10,
            0,
            2,
            2,
            0,
            "history-hash")
        {
            SpanGroup = spanGroup,
            MetricDefinitionHash = "metric-hash",
            Unit = "MiB",
            SeriesKey = "series"
        };
}
