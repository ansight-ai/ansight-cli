using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Files;

// The Explorer uses a file provider, while app.* APIs continue to require an SDK.
internal sealed class LiveSessionFileService(
    IRuntimeState state, AppToolService appTools, Func<string, DevicePlatformProbe?> resolveProvider,
    Func<string, JsonObject, CancellationToken, Task<RequestResult>> captureFile)
{
    public bool IsExternal(string sessionId)
        => state.TryGetSessionContext(sessionId, out var session) && session?.CaptureSource == WorkspaceExecutionModes.Device;

    private DevicePlatformProbe RequireProvider(string sessionId)
    {
        if (!state.IsDeviceSessionActive(sessionId)) throw new IOException("Live sandbox access ended with this capture. Saved artifacts remain available.");
        return resolveProvider(sessionId) ?? throw new IOException("The external sandbox provider is unavailable.");
    }

    public async Task<RuntimeAppToolResponse> CallAsync(string sessionId, string operation,
        JsonObject arguments, CancellationToken cancellationToken)
    {
        if (!IsExternal(sessionId))
            return await appTools.CallWithCatalogAsync(sessionId, operation, arguments, cancellationToken).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var token = deadline.Token;
            var provider = RequireProvider(sessionId);
            var root = arguments["root"]?.GetValue<string>() ?? "data";
            var path = arguments["path"]?.GetValue<string>() ?? "";
            JsonObject result;
            string? artifactId = null;
            if (operation == "files.list_directory")
            {
                var recursive = arguments["recursive"]?.GetValue<bool>() == true;
                var includeHidden = arguments["includeHidden"]?.GetValue<bool>() == true;
                var maximum = Math.Clamp(arguments["maxEntries"]?.GetValue<int>() ?? 500, 1, 1000);
                var maximumDepth = recursive ? Math.Clamp(arguments["maxDepth"]?.GetValue<int>() ?? 16, 1, 16) : 1;
                var entries = new JsonArray();
                var queue = new Queue<SandboxDirectory>();
                queue.Enqueue(new SandboxDirectory(path, 0));
                var truncated = false;
                while (queue.TryDequeue(out var directory))
                {
                    var children = await provider.BrowseDirectoryAsync(root, directory.Path, includeHidden,
                        maximum - entries.Count + 1, token).ConfigureAwait(false);
                    foreach (var entry in children)
                    {
                        if (entries.Count >= maximum) { truncated = true; break; }
                        entries.Add(EntryJson(root, entry));
                        if (recursive && entry.Kind == "directory")
                        {
                            if (directory.Depth + 1 < maximumDepth) queue.Enqueue(new SandboxDirectory(entry.RelativePath, directory.Depth + 1));
                            else truncated = true;
                        }
                    }
                    if (entries.Count >= maximum) { truncated |= queue.Count > 0; break; }
                }
                var roots = await provider.GetFileRootsAsync(token).ConfigureAwait(false);
                result = new JsonObject
                {
                    ["availableRoots"] = new JsonArray(roots.Select(alias => (JsonNode)new JsonObject { ["alias"] = alias }).ToArray()),
                    ["rootAlias"] = root, ["relativePath"] = path, ["entries"] = entries,
                    ["truncated"] = truncated, ["capturedAtUtc"] = DateTimeOffset.UtcNow,
                    ["provider"] = provider.IsAndroid ? "adb" : "simulator-filesystem"
                };
            }
            else if (operation == "files.download_file" || operation == "files.read_file")
            {
                var chunk = await provider.ReadFileChunkAsync(root, path,
                    arguments["offsetBytes"]?.GetValue<long>() ?? 0,
                    Math.Clamp(arguments["maxBytes"]?.GetValue<int>() ?? 524_288, 1, 1_048_576),
                    arguments["expectedVersion"]?.GetValue<string>(), token).ConfigureAwait(false);
                result = EntryJson(root, chunk.File);
                result["fileName"] = chunk.File.Name;
                result["base64"] = Convert.ToBase64String(chunk.Bytes);
                result["bytesRead"] = chunk.Bytes.Length;
                result["hasMore"] = chunk.HasMore;
                result["truncated"] = chunk.HasMore;
                result["version"] = chunk.File.Version;
                result["provider"] = provider.FileProvider;
            }
            else if (operation == BinaryFileDownloadManager.BeginBinaryDownloadToolId)
            {
                var captured = await captureFile("ansight_capture_sandbox_file", new JsonObject
                {
                    ["sessionId"] = sessionId, ["root"] = root, ["path"] = path, ["maximumBytes"] = 67_108_864
                }, token).ConfigureAwait(false);
                if (captured.IsError || captured.Payload?["isError"]?.GetValue<bool>() == true)
                    throw new IOException(captured.Payload?["structuredContent"]?["message"]?.GetValue<string>() ?? "The file could not be saved to the timeline.");
                result = captured.Payload?["structuredContent"]?.DeepClone().AsObject() ?? new JsonObject();
                artifactId = result["snapshotId"]?.GetValue<string>();
                if (artifactId is null) throw new IOException("The capture did not produce a retained artifact.");
            }
            else throw new IOException("This file operation is unavailable through the external provider.");
            RequireProvider(sessionId);
            return new RuntimeAppToolResponse(true, "Sandbox file operation completed.", new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.ResultType, Id = Guid.NewGuid().ToString("N"), SessionId = sessionId,
                Payload = new JsonObject { ["success"] = true, ["result"] = result }
            }, artifactId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new RuntimeAppToolResponse(false, exception.Message, null);
        }
    }

    public async Task<ExternalSandboxCopy> CopyAsync(string sessionId, string? root, string path,
        bool includeDatabaseSidecars, CancellationToken token)
    {
        var provider = RequireProvider(sessionId);
        root ??= "data";
        DevicePlatformProbe.ValidateRelativePath(path);
        var directory = Path.Combine(Path.GetTempPath(), "ansight-file-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var copy = new ExternalSandboxCopy(directory, Path.Combine(directory, Path.GetFileName(path)));
        try
        {
            var files = new List<DeviceSandboxEntry> { await provider.GetFileInfoAsync(root, path, token).ConfigureAwait(false) };
            if (includeDatabaseSidecars)
            {
                var siblings = await provider.BrowseDirectoryAsync(root, Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "", true, 4097, token).ConfigureAwait(false);
                if (siblings.Count > 4096) throw new IOException("This folder contains too many files to check SQLite sidecars safely.");
                if (siblings.Any(file => file.RelativePath == path + "-journal" && file.SizeBytes > 0))
                    throw new IOException("The database has a rollback journal. Finish the app's database write and refresh the preview.");
                files.AddRange(siblings.Where(file => file.RelativePath == path + "-wal" && file.SizeBytes > 0));
            }
            foreach (var file in files)
            {
                if (file.Kind != "file") throw new IOException("Only regular sandbox files can be previewed.");
                await using var output = File.Create(Path.Combine(directory, file.Name));
                long offset = 0;
                do
                {
                    var chunk = await provider.ReadFileChunkAsync(root, file.RelativePath, offset, 1_048_576, file.Version, token).ConfigureAwait(false);
                    await output.WriteAsync(chunk.Bytes, token).ConfigureAwait(false);
                    offset += chunk.Bytes.Length;
                    if (!chunk.HasMore) break;
                    if (chunk.Bytes.Length == 0) throw new IOException("The file transfer ended before the file was complete.");
                } while (true);
            }
            foreach (var file in files)
                if ((await provider.GetFileInfoAsync(root, file.RelativePath, token).ConfigureAwait(false)).Version != file.Version)
                    throw new IOException("The database changed while copying. Refresh the preview when writes have finished.");
            // Detect a newly created or removed WAL as well as changes to an existing one.
            if (includeDatabaseSidecars)
            {
                var siblings = await provider.BrowseDirectoryAsync(root, Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "", true, 4097, token).ConfigureAwait(false);
                if (siblings.Count > 4096 || siblings.Any(file => file.RelativePath == path + "-journal" && file.SizeBytes > 0)
                    || siblings.Any(file => file.RelativePath == path + "-wal" && file.SizeBytes > 0) != files.Any(file => file.RelativePath == path + "-wal"))
                    throw new IOException("Database sidecars changed while copying. Refresh the preview when writes have finished.");
            }
            RequireProvider(sessionId);
            return copy;
        }
        catch { copy.Dispose(); throw; }
    }

    private static JsonObject EntryJson(string root, DeviceSandboxEntry file) => new()
    {
        ["name"] = file.Name, ["relativePath"] = file.RelativePath, ["rootAlias"] = root,
        ["kind"] = file.Kind, ["sizeBytes"] = file.SizeBytes,
        ["lastModifiedUtc"] = file.LastModifiedUtc, ["fileExtension"] = Path.GetExtension(file.Name),
        ["mimeType"] = FileVisualizationService.ResolveMimeType(Path.GetExtension(file.Name).ToLowerInvariant())
    };

    private sealed record SandboxDirectory(string Path, int Depth);
}

internal sealed class ExternalSandboxCopy(string directory, string path) : IDisposable
{
    public string Path { get; } = path;
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
