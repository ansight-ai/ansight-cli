using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class RepositoryTaskDiscoveryDiagnosticsTests
{
    [Fact]
    public void DiscoveryDiagnostics_ReportsInventoryAndReasonsWithoutChangingResults()
    {
        var loadResult = new RepositoryTaskLoadResult(
            [CreateTask("open-weather", "weather"), CreateTask("weather-backup", "weather"), CreateTask("delete-account", "account")], []);
        var regular = RepositoryTaskProtocol.BuildDiscoveryResult(loadResult, "app", "session", "weather", null, 1);
        var traced = RepositoryTaskProtocol.BuildDiscoveryResult(loadResult, "app", "session", "weather", null, 1, includeDiagnostics: true);

        Assert.Null(regular["diagnostics"]);
        Assert.True(JsonNode.DeepEquals(regular["tasks"], traced["tasks"]));
        var diagnostics = Assert.IsType<JsonObject>(traced["diagnostics"]);
        Assert.Equal(3, diagnostics["availableTaskCount"]?.GetValue<int>());
        Assert.Equal(3, Assert.IsType<JsonArray>(diagnostics["availableTaskIds"]).Count);
        var candidates = Assert.IsType<JsonArray>(diagnostics["candidates"]).OfType<JsonObject>().ToArray();
        Assert.Single(candidates, item => item["reason"]?.GetValue<string>() == "returned");
        Assert.Single(candidates, item => item["reason"]?.GetValue<string>() == "discovery-result-limit");
        var excluded = Assert.Single(candidates, item => item["taskId"]?.GetValue<string>() == "delete-account");
        Assert.NotEqual("returned", excluded["reason"]?.GetValue<string>());
        Assert.NotNull(excluded["score"]);
        Assert.NotNull(excluded["coverage"]);
        Assert.False(diagnostics["truncated"]?.GetValue<bool>());
    }

    [Fact]
    public void DiscoveryDiagnostics_BoundsInventoryWhileKeepingExactTotal()
    {
        var loadResult = new RepositoryTaskLoadResult(Enumerable.Range(1, 205).Select(index => CreateTask($"weather-{index}", "weather")).ToArray(), []);
        var result = RepositoryTaskProtocol.BuildDiscoveryResult(loadResult, "app", "session", "weather", null, 1, includeDiagnostics: true);

        var diagnostics = Assert.IsType<JsonObject>(result["diagnostics"]);
        Assert.Equal(205, diagnostics["availableTaskCount"]?.GetValue<int>());
        Assert.Equal(200, Assert.IsType<JsonArray>(diagnostics["availableTaskIds"]).Count);
        Assert.Equal(200, Assert.IsType<JsonArray>(diagnostics["candidates"]).Count);
        Assert.True(diagnostics["truncated"]?.GetValue<bool>());
    }

    private static RepositoryTaskDefinition CreateTask(string taskId, string feature) => new(
        "/repository", $"/repository/ansight/tasks/{taskId}.ts", taskId, 1, "app", taskId, taskId,
        feature, [feature], new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        null, new Dictionary<string, RepositoryTaskHostToolDescriptor>(StringComparer.Ordinal), TimeSpan.FromSeconds(10), 4);
}
