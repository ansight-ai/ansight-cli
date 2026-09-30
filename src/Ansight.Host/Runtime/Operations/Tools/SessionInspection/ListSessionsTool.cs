using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class ListSessionsTool : Operation
{
    public ListSessionsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_list_sessions";

    protected override string Title => "List Sessions";

    protected override string Description => "List captured Ansight sessions with app, device, and capture metadata. Set liveOnly to true to discover currently connected sessions.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: BuildProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(RequestResult.ToolResult(
            SessionListingPayloads.BuildListSessionsPayload(runtimeState, sessionResolver, arguments),
            isError: false));

    private static Dictionary<string, ToolSchema> BuildProperties()
    {
        var properties = new Dictionary<string, ToolSchema>
        {
            ["appId"] = ToolSchema.String("Optional app id to filter results.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("Include historical sessions. Defaults to true.", nullable: true),
            ["liveOnly"] = ToolSchema.Boolean("Restrict results to currently connected live sessions.", nullable: true),
            ["hasLogs"] = ToolSchema.Boolean("Filter to sessions that have captured logs.", nullable: true),
            ["hasTelemetry"] = ToolSchema.Boolean("Filter to sessions that have captured telemetry.", nullable: true),
            ["limit"] = ToolSchema.Integer("Maximum number of sessions to return. Defaults to 100, max 1000.", nullable: true)
        };
        SessionListingFilters.AddProperties(properties);
        return properties;
    }
}
