namespace Ansight.Host.Files;

public sealed partial class FileVisualizationService
{
    private async Task<FileVisualization> InspectExternalFileAsync(string sessionId, string? root, string path,
        bool forceText, bool allowLargeFile, CancellationToken token)
    {
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var kind = forceText ? FileViewerKinds.Text : Classify(extension, null);
            if (IsStreamingViewer(kind))
            {
                var probe = await Runtime.LiveFiles.CallAsync(sessionId, "files.download_file", new System.Text.Json.Nodes.JsonObject
                {
                    ["root"] = root ?? "data", ["path"] = path, ["maxBytes"] = 1
                }, token).ConfigureAwait(false);
                if (!TryReadToolResult(probe, out var metadata, out var error)) return FileVisualization.Failure(error);
                return new FileVisualization(true, "External file stream ready.", kind, Path.GetFileName(path), extension,
                    ResolveMimeType(extension), ResolveLanguage(kind, extension), ReadInt64(metadata, "sizeBytes"), 0, false, null, null, null);
            }
            using var copy = await Runtime.LiveFiles.CopyAsync(sessionId, root, path, kind == FileViewerKinds.Sqlite, token).ConfigureAwait(false);
            if (!forceText && kind != FileViewerKinds.Sqlite && await IsSqliteFileAsync(copy.Path, token).ConfigureAwait(false))
            {
                using var database = await Runtime.LiveFiles.CopyAsync(sessionId, root, path, true, token).ConfigureAwait(false);
                return await InspectCopyAsync(database.Path).ConfigureAwait(false);
            }
            return await InspectCopyAsync(copy.Path).ConfigureAwait(false);

            async Task<FileVisualization> InspectCopyAsync(string localPath)
            {
                var preview = await InspectLocalFileCoreAsync(localPath, Path.GetFileName(path), null, forceText, allowLargeFile, token).ConfigureAwait(false);
                return preview with
                {
                    Message = preview.IsSuccess ? "External file preview ready. Database queries read a local copy of the sandbox files." : preview.Message,
                    DatabaseSchema = preview.DatabaseSchema is { } schema ? schema with { DatabasePath = path } : null
                };
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return FileVisualization.Failure(exception.Message);
        }
    }
}
