using System.Text.Json.Nodes;
using Ansight.Infrastructure;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class ImportSessionArchiveTool : Operation
{
    public ImportSessionArchiveTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_import_session_archive";

    protected override string Title => "Import Session Archive";

    protected override string Description => "Import a native Ansight session archive ZIP into Ansight.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["archiveFilePath"] = ToolSchema.String("Required host-local Ansight session archive ZIP path."),
            ["replaySourceKind"] = ToolSchema.String("Optional replay source kind attached to the imported session.", nullable: true),
            ["replaySourceDisplayName"] = ToolSchema.String("Optional replay source display name attached to the imported session.", nullable: true),
            ["replaySourceDetail"] = ToolSchema.String("Optional replay source detail attached to the imported session.", nullable: true),
            ["replaySourceId"] = ToolSchema.String("Optional replay source id attached to the imported session.", nullable: true)
        },
        required: ["archiveFilePath"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var archiveFilePath = NormalizeOptionalString(arguments?["archiveFilePath"]?.GetValue<string>());
        if (archiveFilePath is null)
        {
            return Task.FromResult(ToolError("archiveFilePath is required."));
        }

        var result = StandardSessionArchiveExporter.Import(runtimeState, archiveFilePath, BuildReplaySource(arguments));
        if (!result.IsSuccess || result.ImportedSession is null)
        {
            return Task.FromResult(RequestResult.ToolResult(
                new JsonObject
                {
                    ["message"] = result.Message,
                    ["archiveFilePath"] = archiveFilePath
                },
                isError: true));
        }

        knownAppStore.EnsureKnown(
            result.ImportedSession.AppId,
            ResolveImportedAppName(result.ImportedSession),
            OperationDefaults.DefaultKnownAppIconGlyph,
            seenAtUtc: DateTimeOffset.UtcNow);

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = result.Message,
                ["archiveFilePath"] = archiveFilePath,
                ["importedSession"] = SessionReviewContext.BuildSessionHeaderPayload(result.ImportedSession, isLive: false)
            },
            isError: false));
    }

    private static SessionReplaySource? BuildReplaySource(JsonObject? arguments)
    {
        var kind = NormalizeOptionalString(arguments?["replaySourceKind"]?.GetValue<string>());
        var displayName = NormalizeOptionalString(arguments?["replaySourceDisplayName"]?.GetValue<string>());
        var detail = NormalizeOptionalString(arguments?["replaySourceDetail"]?.GetValue<string>());
        var sourceId = NormalizeOptionalString(arguments?["replaySourceId"]?.GetValue<string>());
        if (kind is null && displayName is null && detail is null && sourceId is null)
        {
            return null;
        }

        return new SessionReplaySource
        {
            Kind = kind ?? "operation-import",
            DisplayName = displayName ?? "Operation import",
            Detail = detail,
            SourceId = sourceId
        };
    }

    private static string ResolveImportedAppName(AppSessionSnapshot snapshot)
        => snapshot.DeviceProfile?.App?.AppName
           ?? snapshot.ClientName;
}
