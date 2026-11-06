using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Tasks;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;

internal sealed class ListRepositoryTasksTool : RepositoryTaskTool
{
    public ListRepositoryTasksTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_list_tasks";

    protected override string Title => "List Repository Tasks";

    protected override string Description =>
        "Search trusted named tasks from the selected app repository's ansight/tasks directory by focused keywords or feature.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Optional selected Ansight session.", nullable: true),
            ["appId"] = ToolSchema.String("Optional app id when no session is supplied.", nullable: true),
            ["query"] = ToolSchema.String(
                "Focused intent words ranked against task id, title, description, feature, keywords, and declared input schema values. Behavioral synonyms and conservative Levenshtein typo matching are applied; concrete runtime values may remain unmatched.",
                nullable: true),
            ["feature"] = ToolSchema.String("Optional feature or domain filter.", nullable: true),
            ["maxResults"] = ToolSchema.Integer("Maximum matching tasks. Defaults to 10 and is capped at 20.", nullable: true),
            ["includeDiagnostics"] = ToolSchema.Boolean("Include bounded task inventory and discovery decisions for tracing.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryResolveRepository(
            arguments,
            requireLiveSession: false,
            out var session,
            out var app,
            out var loadResult,
            out var errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage));
        }

        var query = NormalizeOptionalString(arguments?["query"]?.GetValue<string>());
        var feature = NormalizeOptionalString(arguments?["feature"]?.GetValue<string>());
        var maxResults = Math.Clamp(
            arguments?["maxResults"]?.GetValue<int>() ?? RepositoryTaskProtocol.DefaultMaximumDiscoveryResults,
            1,
            RepositoryTaskProtocol.MaximumDiscoveryResults);

        return Task.FromResult(RequestResult.ToolResult(
            RepositoryTaskProtocol.BuildDiscoveryResult(
                loadResult!,
                app!.AppId,
                session?.SessionId ?? string.Empty,
                query,
                feature,
                maxResults,
                includeDiagnostics: arguments?["includeDiagnostics"]?.GetValue<bool>() == true),
            isError: false));
    }
}
