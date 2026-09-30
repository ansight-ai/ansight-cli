using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Fact]
    public async Task Executor_GrantsOnlyRequestedRunSecretsWithoutPersistingTheirValues()
    {
        var module = CreatePassingModule()
            .Replace("{ input, ansight, app, expect }", "{ input, ansight, app, expect, secrets }", StringComparison.Ordinal)
            .Replace("const found = await ansight.ui.find({",
                "expect(secrets.require('login_password').length, { id: 'secret-present' }).toBe(8);\n"
                + "             expect(secrets.get('not_granted'), { id: 'secret-absent' }).toBeUndefined();\n"
                + "             const found = await ansight.ui.find({", StringComparison.Ordinal);
        using var repository = CreateRepository(module);
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        Assert.True(RepositoryTaskInputValidator.TryValidateAndApplyDefaults(
            task.InputSchema, null, out var input, out var error), error);
        var executor = CreateExecutor((toolName, arguments, _) => Task.FromResult(
            RequestResult.ToolResult(toolName == "ansight_find_ui"
                ? new JsonObject { ["value"] = arguments["text"]?.DeepClone() }
                : new JsonObject { ["payload"] = new JsonObject { ["result"] = new JsonObject { ["ready"] = true } } },
                isError: false)));

        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest(
            "run-secrets", task, "session-1", input, "correlation-secrets")
        {
            SecretValues = new Dictionary<string, string> { ["login_password"] = "p@ssword" }
        }, CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        Assert.DoesNotContain("p@ssword", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }
}
