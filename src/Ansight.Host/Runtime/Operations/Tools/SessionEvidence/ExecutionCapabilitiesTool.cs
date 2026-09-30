using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class ExecutionCapabilitiesTool(OperationServices services, bool require) : Operation(services)
{
    private readonly DeviceSessionEvidence? deviceEvidence = services.DeviceEvidence;
    public override string Name => require ? "ansight_require_capabilities" : "ansight_get_execution_capabilities";
    protected override string Title => require ? "Require execution capabilities" : "Get execution capabilities";
    protected override string Description => "Resolve current host and SDK capabilities for the exact session. Unsupported requirements fail without retry.";
    protected override JsonObject InputSchema => ToolSchema.Object(properties: new Dictionary<string, ToolSchema>
    {
        ["sessionId"] = ToolSchema.String("Exact session identifier."),
        ["capabilities"] = ToolSchema.Array(ToolSchema.String("Required host capability."), nullable: true),
        ["appTools"] = ToolSchema.Array(ToolSchema.String("Required advertised SDK tool ID."), nullable: true)
    }, additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var id = arguments?["sessionId"]?.GetValue<string>();
        if (id is null || !runtimeState.TryGetSessionSnapshot(id, out var session)) return ToolError("Session is unavailable.");
        var snapshot = await ExecutionCapabilities.ResolveAsync(runtimeState, appToolBridge, session!, ToolExecutionCancellation.Current, deviceEvidence).ConfigureAwait(false);
        if (require)
        {
            var requirementArguments = arguments!.DeepClone().AsObject();
            requirementArguments.Remove("sessionId");
            var requirements = JsonSerializer.Deserialize<ExecutionRequirements>(requirementArguments.ToJsonString(), JsonUtil.Compact)!;
            requirements.Validate(2);
            if (ExecutionCapabilities.Missing(snapshot, requirements) is { } error) return RequestResult.ToolResult(error, isError: true);
        }
        return RequestResult.ToolResult(snapshot, isError: false);
    }
}
