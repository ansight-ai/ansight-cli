using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetListCapturesTool : DotNetOperation
{
    public DotNetListCapturesTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_list_captures";

    protected override string Title => "List .NET Trace Captures";

    protected override string Description => "List persisted Ansight .NET captures and imported NetTrace artifacts with capture state and provenance.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["appId"] = ToolSchema.String("Optional exact app id filter.", nullable: true),
            ["limit"] = ToolSchema.Integer("Maximum captures to return. Defaults to 100, maximum 1000.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!ArgumentReader.TryReadPositiveLimit(arguments, "limit", 100, 1000, out var limit, out var error))
        {
            return Completed(ToolError(error!));
        }

        var appId = arguments?["appId"]?.GetValue<string>()?.Trim();
        var captures = Services.Profiling.ListCaptures()
            .Where(capture => string.IsNullOrWhiteSpace(appId)
                || string.Equals(capture.AppId, appId, StringComparison.Ordinal))
            .Take(limit)
            .ToArray();

        return Completed(ToolSuccess(new JsonObject
        {
            ["captureCount"] = captures.Length,
            ["captures"] = JsonSerializer.SerializeToNode(captures, JsonUtil.Compact)
        }));
    }
}
