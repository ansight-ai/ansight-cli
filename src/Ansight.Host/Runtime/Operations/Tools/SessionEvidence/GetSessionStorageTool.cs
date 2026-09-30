using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class GetSessionStorageTool : Operation
{
    public GetSessionStorageTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_session_storage";

    protected override string Title => "Get Session Storage";

    protected override string Description => "Return persisted cache size and a best-effort file-size breakdown for a captured session.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: SessionReviewToolSchemas.ReviewSessionProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Select a session."));
        }

        var hasCacheSize = runtimeState.TryGetSessionCacheSizeBytes(snapshot!.SessionId, out var cacheSizeBytes);
        var fileBreakdown = BuildFileBreakdownPayload(snapshot);
        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot, SessionReviewContext.IsLiveSession(sessionResolver, snapshot)),
                ["cacheSizeBytes"] = hasCacheSize ? cacheSizeBytes : null,
                ["cacheSizeAvailable"] = hasCacheSize,
                ["files"] = fileBreakdown,
                ["message"] = hasCacheSize
                    ? $"Session cache size is {cacheSizeBytes} bytes."
                    : "Session cache size is unavailable."
            },
            isError: false));
    }

    private JsonObject BuildFileBreakdownPayload(AppSessionSnapshot snapshot)
    {
        var capturesRootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(applicationPaths);
        var sessionDirectoryPath = SessionImageArtifactPath.ResolveSessionDirectoryPath(
            capturesRootPath,
            snapshot.AppId,
            snapshot.SessionId);
        var screenshotBytes = 0L;
        var screenshotFileCount = 0;
        var missingScreenshotFileCount = 0;
        foreach (var frame in snapshot.Images)
        {
            var filePath = SessionImageArtifactPath.ResolveCapturedImagePath(
                capturesRootPath,
                snapshot.AppId,
                snapshot.SessionId,
                frame);
            if (!TryGetFileSize(filePath, out var fileSizeBytes))
            {
                missingScreenshotFileCount++;
                continue;
            }

            screenshotFileCount++;
            screenshotBytes += fileSizeBytes;
        }

        var appIconBytes = 0L;
        var hasAppIconFile = false;
        if (snapshot.AppIcon is not null)
        {
            var appIconPath = SessionAppIconArtifactPath.ResolveSessionIconPath(
                capturesRootPath,
                snapshot.AppId,
                snapshot.SessionId,
                snapshot.AppIcon);
            hasAppIconFile = TryGetFileSize(appIconPath, out appIconBytes);
        }

        var artifactsRootPath = Path.Combine(sessionDirectoryPath, "artifacts");
        var artifactFiles = CountDirectoryFiles(artifactsRootPath);
        var sessionDirectoryFiles = CountDirectoryFiles(sessionDirectoryPath);
        return new JsonObject
        {
            ["sessionDirectoryPath"] = sessionDirectoryPath,
            ["sessionDirectoryExists"] = Directory.Exists(sessionDirectoryPath),
            ["sessionDirectoryBytes"] = sessionDirectoryFiles.SizeBytes,
            ["sessionFileCount"] = sessionDirectoryFiles.FileCount,
            ["screenshotsBytes"] = screenshotBytes,
            ["screenshotFileCount"] = screenshotFileCount,
            ["missingScreenshotFileCount"] = missingScreenshotFileCount,
            ["artifactsDirectoryPath"] = artifactsRootPath,
            ["artifactsDirectoryExists"] = Directory.Exists(artifactsRootPath),
            ["artifactFilesBytes"] = artifactFiles.SizeBytes,
            ["artifactFileCount"] = artifactFiles.FileCount,
            ["appIconBytes"] = hasAppIconFile ? appIconBytes : null,
            ["appIconFileExists"] = hasAppIconFile
        };
    }

    private static bool TryGetFileSize(string filePath, out long fileSizeBytes)
    {
        fileSizeBytes = 0;
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            fileSizeBytes = new FileInfo(filePath).Length;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DirectoryFileCount CountDirectoryFiles(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return new DirectoryFileCount(0, 0);
        }

        long sizeBytes = 0;
        var fileCount = 0;
        try
        {
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    sizeBytes += new FileInfo(filePath).Length;
                    fileCount++;
                }
                catch (IOException)
                {
                    // This exception is an expected fallback for the best-effort operation.
                }
                catch (UnauthorizedAccessException)
                {
                    // This exception is an expected fallback for the best-effort operation.
                }
            }
        }
        catch (IOException)
        {
            // This exception is an expected fallback for the best-effort operation.
        }
        catch (UnauthorizedAccessException)
        {
            // This exception is an expected fallback for the best-effort operation.
        }

        return new DirectoryFileCount(sizeBytes, fileCount);
    }

    private readonly record struct DirectoryFileCount(long SizeBytes, int FileCount);
}
