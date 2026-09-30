using Ansight.Host.Trends;
using Ansight.Host.Tests.Unit.Runtime;

namespace Ansight.Host.Tests.Unit.Trends;

public sealed class WorkspaceTrendsCatalogTests
{
    [Fact]
    public void Load_ParsesInlineSpanMetricsAndRegressionPolicy()
    {
        using var directory = new TemporaryDirectory();
        var trendsPath = Path.Combine(directory.RootPath, "ansight", "trends");
        Directory.CreateDirectory(trendsPath);
        File.WriteAllText(Path.Combine(trendsPath, "login.json"), DefinitionJson());

        var catalog = WorkspaceTrendsCatalog.Load(directory.RootPath);

        Assert.Empty(catalog.Warnings);
        var trends = Assert.Single(catalog.Definitions);
        Assert.Equal("login-trends", trends.TrendsId);
        Assert.True(trends.Enabled);
        Assert.Equal("login.started", trends.Span.Start.Label);
        Assert.Equal("login.completed", trends.Span.End.Label);
        Assert.Equal("ios", Assert.Single(trends.Metrics).Platform);
        var history = Assert.Single(catalog.HistoryDefinitions);
        Assert.Equal("login-trends.fps-p10", history.DecisionId);
        Assert.Equal("ios", history.Platform);
        Assert.Equal(10, history.Baseline.Runs);
        Assert.Equal(5, history.Baseline.MinimumRuns);
        Assert.Equal(["deviceModel", "operatingSystemMajor", "platform"], history.Cohort);
        Assert.Equal("Login frame rate", history.Chart?.Title);
        Assert.Equal("fps", history.Chart?.YAxis.Unit);
    }

    [Fact]
    public void Load_KeepsDisabledDefinitionsDiscoverableButOmitsHistoryPolicies()
    {
        using var directory = new TemporaryDirectory();
        var trendsPath = Path.Combine(directory.RootPath, "ansight", "trends");
        Directory.CreateDirectory(trendsPath);
        File.WriteAllText(
            Path.Combine(trendsPath, "login.json"),
            DefinitionJson().Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n             \"enabled\": false,",
                StringComparison.Ordinal));

        var catalog = WorkspaceTrendsCatalog.Load(directory.RootPath);

        Assert.Empty(catalog.Warnings);
        Assert.False(Assert.Single(catalog.Definitions).Enabled);
        Assert.Empty(catalog.HistoryDefinitions);
    }

    [Fact]
    public void ParseTrends_RejectsANonBooleanEnabledValue()
    {
        var source = DefinitionJson().Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n             \"enabled\": \"no\",",
            StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => WorkspaceTrendsCatalog.ParseTrends(
            "/workspace/ansight/trends",
            "/workspace/ansight/trends/login.json",
            source));

        Assert.Contains("enabled must be a boolean", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseTrends_RequiresTheSpanProperty()
    {
        var source = DefinitionJson().Replace("\"span\":", "\"window\":", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => WorkspaceTrendsCatalog.ParseTrends(
            "/workspace/ansight/trends",
            "/workspace/ansight/trends/login.json",
            source));

        Assert.Contains("span", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseTrends_RejectsInvalidDisplayAxisRange()
    {
        var source = DefinitionJson().Replace(
            "\"maximum\": 120",
            "\"minimum\": 120, \"maximum\": 60",
            StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => WorkspaceTrendsCatalog.ParseTrends(
            "/workspace/ansight/trends",
            "/workspace/ansight/trends/login.json",
            source));

        Assert.Contains("minimum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseTrends_RequiresDirectionWhenBudgetIsAmbiguous()
    {
        var exception = Assert.Throws<InvalidDataException>(() => WorkspaceTrendsCatalog.ParseTrends(
            "/workspace/ansight/trends",
            "/workspace/ansight/trends/memory.json",
            """
            {
              "schemaVersion": 1,
              "id": "memory-trends",
              "appId": "com.example.app",
              "span": {
                "start": { "event": { "label": "load.started" } },
                "end": { "event": { "label": "load.completed" } }
              },
              "metrics": [{
                "id": "slope",
                "channel": { "type": "memory" },
                "statistic": "tailSlopeMiBPerSecond",
                "budget": { "absoluteLte": 1 },
                "regression": { "absolute": 0.5 }
              }]
            }
            """));

        Assert.Contains("worseWhen", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string DefinitionJson()
        => """
           {
             "schemaVersion": 1,
             "id": "login-trends",
             "appId": "com.example.app",
             "span": {
               "start": { "event": { "label": "login.started" } },
               "end": { "event": { "label": "login.completed" } },
               "selection": "exactlyOne",
               "maximumDurationMs": 30000
             },
             "metrics": [{
               "id": "fps-p10",
               "platform": "ios",
               "channel": { "type": "fps" },
               "statistic": "p10",
               "budget": { "gte": 55 },
               "regression": {
                 "percent": 10,
                 "confirmRuns": 2
               },
               "display": {
                 "title": "Login frame rate",
                 "unit": "fps",
                 "fractionDigits": 1,
                 "maximum": 120,
                 "includeZero": true
               }
             }]
           }
           """;
}
