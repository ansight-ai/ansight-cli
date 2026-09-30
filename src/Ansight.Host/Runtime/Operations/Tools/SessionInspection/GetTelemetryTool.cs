using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class GetTelemetryTool : Operation
{
    public GetTelemetryTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_telemetry";

    protected override string Title => "Get Telemetry";

    protected override string Description => "Return captured telemetry filtered by time range, telemetry type, channel id, or channel name.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to inspect.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one matching session exists.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("When resolving by appId, include historical sessions. Defaults to true.", nullable: true),
            ["startUtc"] = ToolSchema.String("Optional inclusive start timestamp in ISO-8601 UTC.", nullable: true, format: "date-time"),
            ["endUtc"] = ToolSchema.String("Optional inclusive end timestamp in ISO-8601 UTC.", nullable: true, format: "date-time"),
            ["types"] = ToolSchema.Array(
                ToolSchema.String("Telemetry type, for example fps or memory."),
                description: "Optional telemetry type filters. Other channel types use a slug derived from the channel name.",
                nullable: true),
            ["channelIds"] = ToolSchema.Array(
                ToolSchema.Integer("Channel id."),
                description: "Optional channel ids to include.",
                nullable: true),
            ["channelNames"] = ToolSchema.Array(
                ToolSchema.String("Exact channel name, case-insensitive."),
                description: "Optional channel names to include.",
                nullable: true),
            ["limit"] = ToolSchema.Integer("Maximum number of matching samples to return. Defaults to 2000, max 10000.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(SessionTelemetryInspection.BuildGetTelemetryResult(sessionResolver, arguments));
}
