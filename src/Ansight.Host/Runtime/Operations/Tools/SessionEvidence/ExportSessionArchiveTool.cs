using System.Text.Json.Nodes;
using Ansight.Infrastructure;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class ExportSessionArchiveTool : Operation
{
    public ExportSessionArchiveTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_export_session_archive";

    protected override string Title => "Export Session Archive";

    protected override string Description => "Write a native Ansight session archive ZIP that can be imported back into Ansight.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: ExportProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Select a session to export."));
        }

        if (SessionReviewContext.IsLiveSession(sessionResolver, snapshot!))
        {
            return Task.FromResult(ToolError("Live sessions can be exported after recording finishes."));
        }

        var archiveFilePath = NormalizeOptionalString(arguments?["archiveFilePath"]?.GetValue<string>())
                              ?? ResolveDefaultArchivePath(applicationPaths, snapshot!);
        var result = StandardSessionArchiveExporter.Export(applicationPaths, snapshot!, archiveFilePath);
        if (!result.IsSuccess)
        {
            return Task.FromResult(RequestResult.ToolResult(
                new JsonObject
                {
                    ["message"] = result.Message,
                    ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, isLive: false),
                    ["archiveFilePath"] = archiveFilePath
                },
                isError: true));
        }

        var archiveInfo = new FileInfo(archiveFilePath);
        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = result.Message,
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, isLive: false),
                ["archiveFilePath"] = archiveFilePath,
                ["archiveSizeBytes"] = archiveInfo.Length
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> ExportProperties()
    {
        var properties = SessionReviewToolSchemas.ReviewSessionProperties();
        properties["archiveFilePath"] = ToolSchema.String("Optional host-local ZIP destination. Defaults to Ansight's session export directory.", nullable: true);
        return properties;
    }

    private static string ResolveDefaultArchivePath(IApplicationPaths applicationPaths, AppSessionSnapshot snapshot)
    {
        var directoryPath = Path.Combine(
            applicationPaths.ApplicationDataPath,
            SessionEvidenceDefaults.SessionExportsDirectoryName);
        var fileName = $"{FileNameUtil.Sanitize(snapshot.AppId)}-{FileNameUtil.Sanitize(snapshot.SessionId)}-native-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip";
        return Path.Combine(directoryPath, fileName);
    }
}
