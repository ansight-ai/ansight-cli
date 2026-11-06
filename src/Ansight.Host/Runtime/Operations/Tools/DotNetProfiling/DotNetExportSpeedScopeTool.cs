using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetExportSpeedScopeTool : DotNetOperation
{
    public DotNetExportSpeedScopeTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_export_speedscope";

    protected override string Title => "Export .NET SpeedScope Profile";

    protected override string Description => "Resolve the derived SpeedScope JSON artifact for a .NET capture without treating it as the authoritative raw trace.";

    protected override JsonObject InputSchema => DotNetCaptureToolSchemas.CaptureIdSchema();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var captureId = arguments?["captureId"]?.GetValue<string>()?.Trim();

        if (string.IsNullOrWhiteSpace(captureId))
        {
            return Completed(ToolError("captureId is required."));
        }

        if (!Services.Profiling.CaptureStore.TryLoadManifest(captureId, out var manifest)
            || manifest is null)
        {
            return Completed(ToolError($".NET trace capture '{captureId}' was not found."));
        }

        if (!Services.Profiling.CaptureStore.TryResolveArtifactPath(
            manifest,
            DotNetProfilingEngine.SpeedScopeArtifactKind,
            out var filePath)
            || filePath is null)
        {
            return Completed(ToolError($"Capture '{captureId}' does not contain a SpeedScope derivative."));
        }

        var artifact = manifest.Artifacts.First(candidate =>
            string.Equals(candidate.Kind, DotNetProfilingEngine.SpeedScopeArtifactKind, StringComparison.Ordinal));

        return Completed(ToolSuccess(new JsonObject
        {
            ["captureId"] = captureId,
            ["filePath"] = filePath,
            ["artifact"] = JsonSerializer.SerializeToNode(artifact, JsonUtil.Compact),
            ["authoritative"] = false,
            ["rawArtifactKind"] = DotNetProfilingEngine.NetTraceArtifactKind
        }));
    }
}
