using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class GetLogsTool : Operation
{
    public GetLogsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_logs";

    protected override string Title => "Get Session Logs";

    protected override string Description => "Return session logs filtered by time range, minimum verbosity, and tags.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to inspect.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one matching session exists.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("When resolving by appId, include historical sessions. Defaults to true.", nullable: true),
            ["startUtc"] = ToolSchema.String("Optional inclusive start timestamp in ISO-8601 UTC.", nullable: true, format: "date-time"),
            ["endUtc"] = ToolSchema.String("Optional inclusive end timestamp in ISO-8601 UTC.", nullable: true, format: "date-time"),
            ["minimumVerbosity"] = SessionInspectionToolSchemas.MinimumVerbositySchema(),
            ["streamIds"] = SessionInspectionToolSchemas.StreamIdsSchema(),
            ["tags"] = SessionInspectionToolSchemas.TagsSchema(),
            ["limit"] = ToolSchema.Integer("Maximum number of matching logs to return. Defaults to 200, max 5000.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(SessionLogInspection.BuildGetLogsResult(sessionResolver, arguments));
}
