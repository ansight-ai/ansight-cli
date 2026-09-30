using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class SearchLogsTool : Operation
{
    public SearchLogsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_search_logs";

    protected override string Title => "Search Session Logs";

    protected override string Description => "Search captured Ansight session logs by keyword across Ansight sessions, with optional app, platform, session, time, verbosity, tag, and source filters.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: SessionInspectionToolSchemas.LogReviewFilterProperties(includeRequiredQuery: true)
            .Concat(new Dictionary<string, ToolSchema>
            {
                ["limit"] = ToolSchema.Integer("Maximum number of matching logs to return. Defaults to 200, max 5000.", nullable: true)
            })
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        required: ["query"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(SessionLogInspection.BuildSearchLogsResult(runtimeState, sessionResolver, arguments));
}
