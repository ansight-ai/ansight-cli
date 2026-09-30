using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class ExternalSandboxFileTool(OperationServices services, string action) : Operation(services)
{
    private readonly DeviceSessionEvidence? deviceEvidence = services.DeviceEvidence;
    public override string Name => "ansight_" + action + "_sandbox_file";
    protected override string Title => action + " app sandbox file";
    protected override string Description => "Read or capture a simulator app file externally. Paths are relative to an app root; captures are best-effort live copies.";
    protected override JsonObject InputSchema => ToolSchema.Object(properties: new Dictionary<string, ToolSchema>
    {
        ["sessionId"] = ToolSchema.String("Exact live device session."),
        ["root"] = ToolSchema.String("data, app, or group:<identifier>; Android supports data. Defaults to data.", nullable: true),
        ["path"] = ToolSchema.String("Root-relative file path; empty for listing the root."),
        ["maximumBytes"] = ToolSchema.Integer("Maximum bytes; read defaults to 256 KiB, capture to 16 MiB, maximum 64 MiB.", nullable: true)
    }, additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var sessionId = arguments?["sessionId"]?.GetValue<string>();
        if (sessionId is null || !runtimeState.TryGetSessionSnapshot(sessionId, out var session)) return ToolError("An exact session identifier is required.");
        var root = arguments?["root"]?.GetValue<string>() ?? "data";
        var path = arguments?["path"]?.GetValue<string>() ?? "";
        var limit = arguments?["maximumBytes"]?.GetValue<int>() ?? (action == "read" ? 262_144 : 16_777_216);
        if (limit < 1 || limit > (action == "read" ? 262_144 : 67_108_864)) return ToolError("maximumBytes exceeds the operation's limit.");
        var token = ToolExecutionCancellation.Current;
        try
        {
            var provider = deviceEvidence?.RequireFiles(session!.SessionId)
                ?? throw new InvalidOperationException("External sandbox access is unavailable for this session.");
            if (action == "list")
                return RequestResult.ToolResult(new JsonObject
                {
                    ["root"] = root, ["path"] = path,
                    ["entries"] = new JsonArray((await provider.ListFilesAsync(root, path, token).ConfigureAwait(false)).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
                }, isError: false);
            var started = DateTimeOffset.UtcNow;
            var bytes = await provider.ReadFileAsync(root, path, limit, token).ConfigureAwait(false);
            var result = new JsonObject
            {
                ["root"] = root, ["path"] = path, ["sizeBytes"] = bytes.Length,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["provider"] = provider.FileProvider,
                ["consistency"] = "best-effort-live-copy", ["startedAtUtc"] = started, ["capturedAtUtc"] = DateTimeOffset.UtcNow
            };
            if (action == "read") result["base64"] = Convert.ToBase64String(bytes);
            else
            {
                var id = Guid.NewGuid().ToString("N");
                var directory = Path.Combine(Path.GetTempPath(), "ansight-sandbox-" + id);
                Directory.CreateDirectory(directory);
                try
                {
                    var name = Path.GetFileName(path);
                    await File.WriteAllBytesAsync(Path.Combine(directory, "content"), bytes, token).ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(directory, "capture.json"), result.ToJsonString(), token).ConfigureAwait(false);
                    var snapshot = new SessionArtifactSnapshot
                    {
                        SnapshotId = id, CapturedAtUtc = DateTimeOffset.UtcNow, Source = provider.FileProvider ?? "external-sandbox",
                        RootAlias = root, RelativePath = path, Name = name, Kind = "file", ArtifactDirectoryName = "sandbox-" + id,
                        FileCount = 2, ByteCount = bytes.Length,
                        Entries = [new SessionArtifactEntry
                        {
                            Name = name, RootAlias = root, RelativePath = path, SnapshotRelativePath = "content", ArchiveRelativePath = "content", Kind = "file",
                            SizeBytes = bytes.Length, FileExtension = Path.GetExtension(name)
                        }, new SessionArtifactEntry
                        {
                            Name = "capture.json", RootAlias = root, RelativePath = "capture.json", SnapshotRelativePath = "capture.json", ArchiveRelativePath = "capture.json", Kind = "file"
                        }]
                    };
                    var stored = runtimeState.AddSessionArtifactSnapshot(session!.SessionId, snapshot, directory);
                    if (!stored.IsSuccess) return ToolError(stored.Message);
                    result["snapshotId"] = id;
                }
                finally { Directory.Delete(directory, recursive: true); }
            }
            return RequestResult.ToolResult(result, isError: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return RequestResult.ToolResult(ExecutionCapabilities.Unavailable("files." + (action == "capture" ? "capture" : "read"),
                new JsonObject { ["executionMode"] = session!.CaptureSource }, exception.Message), isError: true);
        }
    }
}
