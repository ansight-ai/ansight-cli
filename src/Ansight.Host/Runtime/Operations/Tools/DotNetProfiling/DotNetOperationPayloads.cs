using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal static class DotNetOperationPayloads
{
    public static JsonObject Manifest(DotNetTraceCaptureManifest manifest)
        => JsonSerializer.SerializeToNode(manifest, JsonUtil.Compact)?.AsObject() ?? new JsonObject();

    public static JsonObject Snapshot(DotNetTraceCaptureSnapshot snapshot)
        => JsonSerializer.SerializeToNode(snapshot, JsonUtil.Compact)?.AsObject() ?? new JsonObject();

    public static JsonObject AnalysisEnvelope(
        ParsedTraceAnalysisResult result,
        string resultName,
        JsonNode? value)
    {
        return new JsonObject
        {
            ["captureId"] = result.Manifest.CaptureId,
            ["analyzerVersion"] = DotNetTraceAnalysisService.AnalyzerVersion,
            ["selection"] = JsonSerializer.SerializeToNode(result.Selection, JsonUtil.Compact),
            ["evidence"] = new JsonObject
            {
                ["artifactKind"] = result.NetTraceArtifact.Kind,
                ["relativePath"] = result.NetTraceArtifact.RelativePath,
                ["sha256"] = result.NetTraceArtifact.Sha256
            },
            [resultName] = value,
            ["warnings"] = JsonSerializer.SerializeToNode(result.Analysis.Warnings, JsonUtil.Compact)
        };
    }
}
