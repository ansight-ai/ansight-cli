using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetImportTraceTool : DotNetOperation
{
    public DotNetImportTraceTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_import_trace";

    protected override string Title => "Import .NET Trace";

    protected override string Description => "Import an existing local .nettrace into Ansight's immutable .NET capture store and derive a SpeedScope profile when possible.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["tracePath"] = ToolSchema.String("Required absolute path to an existing .nettrace file."),
            ["appId"] = ToolSchema.String("Optional Ansight app id for provenance.", nullable: true),
            ["applicationPath"] = ToolSchema.String("Optional originating application artifact path for provenance.", nullable: true)
        },
        required: ["tracePath"],
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var tracePath = arguments?["tracePath"]?.GetValue<string>()?.Trim();

        if (string.IsNullOrWhiteSpace(tracePath))
        {
            return ToolError("tracePath is required.");
        }

        try
        {
            var manifest = await Services.Profiling.ImportTraceAsync(
                tracePath,
                arguments?["appId"]?.GetValue<string>(),
                arguments?["applicationPath"]?.GetValue<string>());

            return ToolSuccess(new JsonObject
            {
                ["captureId"] = manifest.CaptureId,
                ["manifest"] = DotNetOperationPayloads.Manifest(manifest)
            });
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            return ToolError(ex.Message);
        }
    }
}
