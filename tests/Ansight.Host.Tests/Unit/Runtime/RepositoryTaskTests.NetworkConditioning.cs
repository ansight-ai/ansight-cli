using System.Text.Json.Nodes;
using Ansight.Host.Devices.Network;
using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executor_NetworkApiPinsSessionAndCleansUpEvenWithoutJavascriptFinally(bool fail)
    {
        var source = CreateAudioModule();
        source = source[..source.IndexOf("export default", StringComparison.Ordinal)] + $$"""
            export default async function run({ ansight, expect }) {
              const lease = await ansight.device.network.set({ profile: { stall: true }, sessionId: "forged", deviceId: "forged" });
              expect(lease.leaseId, { id: "network-lease-created" }).toBe("test-lease");
              {{(fail ? "throw new Error('test failure');" : "return { applied: true };")}}
            }
            """;
        using var repository = CreateRepository(source);
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var cleaned = false;
        var executor = new JavaScriptRepositoryTaskExecutor("node", (name, arguments, _) =>
        {
            Assert.Equal("ansight_network_conditioning_set", name);
            Assert.Equal("network-session", arguments["sessionId"]!.GetValue<string>());
            Assert.Null(arguments["deviceId"]);
            var scope = Assert.IsType<NetworkConditioningScope>(NetworkConditioningScope.Current);
            Assert.Equal("network-run", scope.OwnerId);
            scope.Register(() => { cleaned = true; return Task.CompletedTask; });
            return Task.FromResult(RequestResult.ToolResult(new JsonObject { ["leaseId"] = "test-lease" }, isError: false));
        }, hostApiSuites: RepositoryJavaScriptApiMethods.StandardHostToolSuites);
        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest("network-run", task, "network-session", new JsonObject(), "network-test"), default);
        Assert.Equal(fail ? RepositoryTaskRunStatus.Error : RepositoryTaskRunStatus.Passed, result.Status);
        Assert.True(cleaned);
        Assert.Null(NetworkConditioningScope.Current);
    }
}
