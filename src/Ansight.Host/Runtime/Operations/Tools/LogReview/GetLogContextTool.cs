using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.LogReview;

internal sealed class GetLogContextTool : Operation
{
    private readonly LogReviewActions logReview;

    public GetLogContextTool(OperationServices services)
        : base(services)
    {
        logReview = new LogReviewActions(services);
    }

    public override string Name => "ansight_get_log_context";

    protected override string Title => "Get Log Context";

    protected override string Description => "Return surrounding logs for a specific log index, event id, or timestamp in a captured session.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to inspect.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one matching session exists.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("When resolving by appId, include historical sessions. Defaults to true.", nullable: true),
            ["platform"] = SessionInspectionToolSchemas.PlatformSchema("Optional platform key filter."),
            ["platforms"] = SessionInspectionToolSchemas.PlatformsSchema(),
            ["timestampUtc"] = ToolSchema.String("Optional target timestamp. The closest log at or after this time is selected.", nullable: true, format: "date-time"),
            ["eventId"] = ToolSchema.String("Optional event id to select as the target log.", nullable: true),
            ["logIndex"] = ToolSchema.Integer("Optional zero-based index in the session's timestamp-ordered log stream.", nullable: true),
            ["before"] = ToolSchema.Integer("Number of logs before the target. Defaults to 20, max 200.", nullable: true),
            ["after"] = ToolSchema.Integer("Number of logs after the target. Defaults to 20, max 200.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(logReview.BuildGetLogContextResult(arguments));
}
