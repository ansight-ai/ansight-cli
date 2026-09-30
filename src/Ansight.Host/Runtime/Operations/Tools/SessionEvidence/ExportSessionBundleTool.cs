using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class ExportSessionBundleTool : Operation
{
    public ExportSessionBundleTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_export_session_bundle";

    protected override string Title => "Export Session Bundle";

    protected override string Description => "Write a ZIP bundle containing a session manifest, timeline evidence, screenshots, visual-tree payloads, and artifact files.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: ExportProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !TouchReviewArgumentReader.TryReadBooleanArgument(arguments, "includeScreenshots", defaultValue: true, out var includeScreenshots, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBooleanArgument(arguments, "includeArtifactFiles", defaultValue: true, out var includeArtifactFiles, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBooleanArgument(arguments, "includeVisualTreePayloads", defaultValue: true, out var includeVisualTreePayloads, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid session bundle export arguments."));
        }

        var archiveFilePath = NormalizeOptionalString(arguments?["archiveFilePath"]?.GetValue<string>())
                              ?? SessionArchiveExporter.ResolveDefaultArchivePath(applicationPaths, snapshot!);
        var options = new SessionBundleExportOptions
        {
            IncludeScreenshots = includeScreenshots,
            IncludeArtifactFiles = includeArtifactFiles,
            IncludeVisualTreePayloads = includeVisualTreePayloads
        };

        try
        {
            SessionArchiveExporter.Export(applicationPaths, snapshot!, archiveFilePath, options);
        }
        catch (Exception exception)
        {
            return Task.FromResult(RequestResult.ToolResult(
                new JsonObject
                {
                    ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                    ["archiveFilePath"] = archiveFilePath,
                    ["message"] = exception.Message
                },
                isError: true));
        }

        var archiveInfo = new FileInfo(archiveFilePath);
        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                ["archiveFilePath"] = archiveFilePath,
                ["archiveSizeBytes"] = archiveInfo.Length,
                ["options"] = new JsonObject
                {
                    ["includeScreenshots"] = options.IncludeScreenshots,
                    ["includeArtifactFiles"] = options.IncludeArtifactFiles,
                    ["includeVisualTreePayloads"] = options.IncludeVisualTreePayloads
                },
                ["message"] = "Session bundle exported."
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> ExportProperties()
    {
        var properties = SessionReviewToolSchemas.ReviewSessionProperties();
        properties["archiveFilePath"] = ToolSchema.String("Optional host-local ZIP destination. Defaults to Ansight's session export directory.", nullable: true);
        properties["includeScreenshots"] = ToolSchema.Boolean("Include captured screenshot image files. Defaults to true.", nullable: true);
        properties["includeArtifactFiles"] = ToolSchema.Boolean("Include persisted app artifact files. Defaults to true.", nullable: true);
        properties["includeVisualTreePayloads"] = ToolSchema.Boolean("Include full visual-tree JSON payload files. Defaults to true.", nullable: true);
        return properties;
    }
}
