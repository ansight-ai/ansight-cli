using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ansight.Host.AppGraphs;
using Ansight.Host.Files;
using Ansight.Host.Trends;
using Ansight.Host.Runtime.BinaryTransfers;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Host.Runtime.Tasks;
using Ansight.Infrastructure.Preferences;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private async Task<bool> TryHandleSessionsDynamicGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken, string[] segments)
    {
        if (isExplorer && segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "export")
        {
            await ExportSessionArchiveAsync(response, Uri.UnescapeDataString(segments[2]), isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "files" && segments[4] == "content")
        {
            var path = request.QueryString["path"]?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException("A live sandbox file path is required.");
            }

            await WriteLiveFileContentAsync(request, response, Uri.UnescapeDataString(segments[2]), request.QueryString["root"], path, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "artifacts" && segments[4] == "applications")
        {
            var path = request.QueryString["path"]?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException("An artifact path is required.");
            }

            var source = await runtime.FileVisualizations.ResolveSessionArtifactContentAsync(Uri.UnescapeDataString(segments[2]), request.QueryString["snapshotId"], path, cancellationToken).ConfigureAwait(false);
            if (!source.IsSuccess || source.FilePath is null)
            {
                await WriteJsonAsync(response, new { message = source.Message }, HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var applications = await ArtifactPlatformApplications.ListAsync(source.FilePath, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, new { applications, message = applications.Count == 0 ? "No installed applications can open this file type." : null }, HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "artifacts" && segments[4] == "content")
        {
            var path = request.QueryString["path"]?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException("An artifact path is required.");
            }

            await WriteArtifactContentAsync(request, response, Uri.UnescapeDataString(segments[2]), request.QueryString["snapshotId"], path, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "visual-tree" && segments[4] == "sources")
        {
            var result = await runtime.LiveVisualTrees.GetSourcesAsync(Uri.UnescapeDataString(segments[2]), cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "cloud-analysis")
        {
            await WriteCloudAnalysisStateAsync(response, Uri.UnescapeDataString(segments[2]), isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "annotations")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            if (!isExplorer && !string.Equals(sessionId, InitialSessionId, StringComparison.Ordinal))
            {
                await WriteJsonAsync(response, new { message = "Session not found." }, HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var snapshot = await runtime.Sessions.LoadSnapshotAsync(sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, snapshot?.Annotations ?? [], snapshot is null ? HttpStatusCode.NotFound : HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 3 && segments[0] == "api" && segments[1] == "sessions")
        {
            await WriteSessionAsync(response, Uri.UnescapeDataString(segments[2]), isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "icon")
        {
            await WriteSessionIconAsync(response, Uri.UnescapeDataString(segments[2]), isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "updates")
        {
            await WriteSessionUpdateAsync(request, response, Uri.UnescapeDataString(segments[2]), isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "storage")
        {
            await WriteSessionStorageBreakdownAsync(response, Uri.UnescapeDataString(segments[2]), isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "frames")
        {
            await WriteFrameAsync(response, Uri.UnescapeDataString(segments[2]), Uri.UnescapeDataString(segments[4]), isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "simulator" && segments[4] == "frame")
        {
            await ProxySimulatorFrameAsync(response, Uri.UnescapeDataString(segments[2]), isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleSessionsDynamicPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken, string[] segments)
    {
        if (segments is ["api", "sessions", _, "local-summary"])
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            if (!isExplorer && !string.Equals(sessionId, InitialSessionId, StringComparison.Ordinal))
            {
                await WriteJsonAsync(response, new { isSuccess = false, message = "Session not found." }, HttpStatusCode.NotFound, false, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var body = await ReadJsonAsync<JsonObject>(request, cancellationToken).ConfigureAwait(false);
            var teamIdText = ReadOptionalString(body, "teamId");
            if (teamIdText is not null && (!Guid.TryParse(teamIdText, out var parsedTeamId) || parsedTeamId == Guid.Empty))
            {
                await WriteJsonAsync(response, new { isSuccess = false, message = "Choose a valid organisation ID." }, HttpStatusCode.BadRequest, false, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var snapshot = await LoadReplaySnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                await WriteJsonAsync(response, new { isSuccess = false, message = "Session not found." }, HttpStatusCode.NotFound, false, cancellationToken).ConfigureAwait(false);
                return true;
            }

            try
            {
                var teamId = teamIdText is null ? (Guid?)null : Guid.Parse(teamIdText);
                var analysis = await LocalSessionSummaryRunner.RunAsync(runtime, snapshot, teamId, cancellationToken).ConfigureAwait(false);
                runtime.SessionEditing.AddAnalysis(sessionId, analysis);
                await WriteJsonAsync(response, new { isSuccess = true, message = "Local session summary saved.", analysisId = analysis.AnalysisId }, HttpStatusCode.Created, false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or IOException or JsonException)
            {
                await WriteJsonAsync(response, new { isSuccess = false, message = exception.Message }, HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        if (segments.Length >= 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "cloud-analysis")
            return await TryHandleCloudAnalysisPostAsync(route, request, response, cancellationToken, segments).ConfigureAwait(false);

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "metadata")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            if (!isExplorer && !string.Equals(sessionId, InitialSessionId, StringComparison.Ordinal))
            {
                await WriteJsonAsync(response, OperationResult.Failure("Session not found."), HttpStatusCode.NotFound, false, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var body = await ReadJsonAsync<LocalSessionMetadataUpdateRequest>(request, cancellationToken).ConfigureAwait(false);
            var result = runtime.SessionEditing.UpdateMetadata(sessionId, body.IsPinned, body.Tags, body.Notes, body.Name);
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.BadRequest, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (isExplorer && segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "disconnect")
        {
            var result = runtime.AppTools.ForceDisconnect(Uri.UnescapeDataString(segments[2]));
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "share")
            return await TryHandleSessionSharePostAsync(route, request, response, cancellationToken, segments).ConfigureAwait(false);

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "normalize")
        {
            var result = runtime.SessionEditing.Normalize(Uri.UnescapeDataString(segments[2]));
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] is "optimize" or "export-desktop")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            if (!isExplorer && !string.Equals(sessionId, InitialSessionId, StringComparison.Ordinal))
            {
                await WriteJsonAsync(response, OperationResult.Failure("Session not found."), HttpStatusCode.NotFound, false, cancellationToken).ConfigureAwait(false);
                return true;
            }
            if (segments[3] == "optimize")
            {
                var body = await ReadJsonAsync<OptimizeSessionRequest>(request, cancellationToken).ConfigureAwait(false);
                await OptimizeSessionAsync(response, sessionId, body, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    var archiveFilePath = await ExportSessionToDesktopAsync(sessionId, false, null, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, new { isSuccess = true, message = $"ZIP saved to {archiveFilePath}", archiveFilePath }, HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    await WriteJsonAsync(response, OperationResult.Failure(exception.Message), HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
                }
            }
            return true;
        }

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "extract")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            var body = await ReadJsonAsync<SessionTimelineExtractRequest>(request, cancellationToken).ConfigureAwait(false);
            var result = runtime.SessionEditing.ExtractTimelineRange(sessionId, body.StartUtc, body.EndUtc, body.Name);
            var responseResult = new SessionTimelineExtractionResult(result.IsSuccess, result.Message, result.ExtractedSession?.SessionId);
            await WriteJsonAsync(response, responseResult, result.IsSuccess ? HttpStatusCode.Created : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "trim")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            var body = await ReadJsonAsync<SessionTimelineTrimRequest>(request, cancellationToken).ConfigureAwait(false);
            if (!TryParseTimelineTrimMode(body.Mode, out var mode))
            {
                await WriteJsonAsync(response, OperationResult.Failure("Select a valid trim action."), HttpStatusCode.BadRequest, false, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (request.AcceptTypes?.Any(type => string.Equals(type.Trim(), "application/x-ndjson", StringComparison.OrdinalIgnoreCase)) == true)
            {
                await StreamTimelineTrimAsync(response, sessionId, body, mode, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var result = runtime.SessionEditing.TrimTimeline(sessionId, body.StartUtc, body.EndUtc, mode);
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "delete")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            if (!isExplorer && !string.Equals(sessionId, InitialSessionId, StringComparison.Ordinal))
            {
                await WriteJsonAsync(response, OperationResult.Failure("Session not found."), HttpStatusCode.NotFound, false, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var result = runtime.SessionEditing.Delete(sessionId);
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "simulator")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            var body = await ReadJsonAsync<JsonObject>(request, cancellationToken).ConfigureAwait(false);
            await ProxySimulatorCommandAsync(response, sessionId, segments[4], body, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "annotations")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            var annotation = await ReadJsonAsync<SessionAnnotation>(request, cancellationToken).ConfigureAwait(false);
            var result = runtime.SessionEditing.UpsertAnnotation(sessionId, annotation);
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.BadRequest, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 6 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "annotations" && segments[5] == "delete")
        {
            var result = runtime.SessionEditing.DeleteAnnotation(Uri.UnescapeDataString(segments[2]), Uri.UnescapeDataString(segments[4]));
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.BadRequest, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "visual-tree" && segments[4] == "capture")
        {
            var body = await ReadJsonAsync<JsonObject>(request, cancellationToken).ConfigureAwait(false);
            var result = await runtime.LiveVisualTrees.CaptureLiveVisualTreeAsync(Uri.UnescapeDataString(segments[2]), ReadOptionalString(body, "toolId"), cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "files" && segments[4] == "list")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            var body = await ReadJsonAsync<JsonObject>(request, cancellationToken).ConfigureAwait(false);
            var recursive = ReadOptionalBoolean(body, "recursive");
            var arguments = new JsonObject
            {
                ["path"] = ReadOptionalString(body, "path") ?? string.Empty,
                ["includeHidden"] = ReadOptionalBoolean(body, "includeHidden"),
                ["recursive"] = recursive,
                ["maxEntries"] = Math.Clamp(ReadOptionalInt32(body, "maxEntries", recursive ? MaximumLiveFileSearchEntries : 500), 1, MaximumLiveFileSearchEntries)
            };
            if (recursive)
            {
                arguments["maxDepth"] = Math.Clamp(ReadOptionalInt32(body, "maxDepth", MaximumLiveFileSearchDepth), 1, MaximumLiveFileSearchDepth);
            }

            var root = ReadOptionalString(body, "root");
            if (!string.IsNullOrWhiteSpace(root))
            {
                arguments["root"] = root.Trim();
            }

            var result = await runtime.LiveFiles.CallAsync(sessionId, "files.list_directory", arguments, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, result, result.Success ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "files" && segments[4] == "read")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            var body = await ReadJsonAsync<JsonObject>(request, cancellationToken).ConfigureAwait(false);
            var path = ReadOptionalString(body, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException("A live sandbox file path is required.");
            }

            var arguments = new JsonObject
            {
                ["path"] = path.Trim(),
                ["maxBytes"] = 512 * 1024,
                ["encoding"] = "auto"
            };
            var root = ReadOptionalString(body, "root");
            if (!string.IsNullOrWhiteSpace(root))
            {
                arguments["root"] = root.Trim();
            }

            var result = await runtime.LiveFiles.CallAsync(sessionId, "files.read_file", arguments, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, result, result.Success ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "files" && segments[4] == "capture")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            var body = await ReadJsonAsync<JsonObject>(request, cancellationToken).ConfigureAwait(false);
            var path = ReadOptionalString(body, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException("A live sandbox file path is required.");
            }

            var arguments = new JsonObject
            {
                ["path"] = path.Trim(),
                ["chunkBytes"] = LiveFileDownloadChunkBytes,
                ["downloadId"] = $"timeline-artifact-{Guid.NewGuid():N}"
            };
            var root = ReadOptionalString(body, "root");
            if (!string.IsNullOrWhiteSpace(root))
            {
                arguments["root"] = root.Trim();
            }

            var result = await runtime.LiveFiles.CallAsync(sessionId, BinaryFileDownloadManager.BeginBinaryDownloadToolId, arguments, cancellationToken).ConfigureAwait(false);
            if (result.Success && string.IsNullOrWhiteSpace(result.ArtifactSnapshotId))
            {
                result = result with
                {
                    Success = false,
                    Message = "The file transfer completed without creating a timeline artifact."
                };
            }

            await WriteJsonAsync(response, result, result.Success ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "files" && segments[4] == "preview")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            var body = await ReadJsonAsync<JsonObject>(request, cancellationToken).ConfigureAwait(false);
            var path = ReadOptionalString(body, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException("A live sandbox file path is required.");
            }

            var result = await runtime.FileVisualizations.InspectLiveFileAsync(sessionId, ReadOptionalString(body, "root"), path, ReadOptionalBoolean(body, "forceText"), ReadOptionalBoolean(body, "allowLargeFile"), cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "files" && segments[4] == "query")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            var body = await ReadJsonAsync<JsonObject>(request, cancellationToken).ConfigureAwait(false);
            var path = ReadOptionalString(body, "path");
            var sql = ReadOptionalString(body, "sql");
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sql))
            {
                throw new InvalidDataException("A live database path and read-only SQL query are required.");
            }

            var result = await runtime.FileVisualizations.QueryLiveDatabaseAsync(sessionId, path, sql, ReadOptionalInt32(body, "maxRows", 100), cancellationToken, sandboxRoot: ReadOptionalString(body, "root")).ConfigureAwait(false);
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "artifacts" && segments[4] is "open" or "reveal" or "export-desktop")
        {
            var body = await ReadJsonAsync<JsonObject>(request, cancellationToken).ConfigureAwait(false);
            var path = ReadOptionalString(body, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException("An artifact path is required.");
            }

            var source = await runtime.FileVisualizations.ResolveSessionArtifactContentAsync(Uri.UnescapeDataString(segments[2]), ReadOptionalString(body, "snapshotId"), path, cancellationToken).ConfigureAwait(false);
            if (!source.IsSuccess || source.FilePath is null)
            {
                await WriteJsonAsync(response, new { message = source.Message }, HttpStatusCode.NotFound, false, cancellationToken).ConfigureAwait(false);
                return true;
            }

            string message;
            string? exportedPath = null;
            switch (segments[4])
            {
                case "open":
                    var applicationId = ReadOptionalString(body, "applicationId");
                    if (string.IsNullOrWhiteSpace(applicationId))
                    {
                        throw new InvalidDataException("Choose an application to open the artifact.");
                    }

                    await ArtifactPlatformApplications.OpenAsync(source.FilePath, applicationId, cancellationToken).ConfigureAwait(false);
                    message = $"Opened {source.FileName}.";
                    break;
                case "reveal":
                    await ArtifactDesktopActions.RevealAsync(source.FilePath, cancellationToken).ConfigureAwait(false);
                    message = $"Showing {source.FileName} in its folder.";
                    break;
                default:
                    exportedPath = await ArtifactDesktopActions.ExportToDesktopAsync(source.FilePath, cancellationToken).ConfigureAwait(false);
                    message = $"Exported {Path.GetFileName(exportedPath)} to Desktop.";
                    break;
            }

            await WriteJsonAsync(response, new { message, path = exportedPath }, HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments is ["api", "artifacts", "list"])
        {
            var body = await ReadJsonAsync<ArtifactListRequest>(request, cancellationToken).ConfigureAwait(false);
            var result = await runtime.ArtifactComparisons.ListAsync(body, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, result, HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments is ["api", "artifacts", "diff"])
        {
            var body = await ReadJsonAsync<ArtifactDiffRequest>(request, cancellationToken).ConfigureAwait(false);
            var result = await runtime.ArtifactComparisons.CompareAsync(body, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, result, HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "artifacts" && segments[4] == "preview")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            var body = await ReadJsonAsync<JsonObject>(request, cancellationToken).ConfigureAwait(false);
            var path = ReadOptionalString(body, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException("An artifact path is required.");
            }

            var result = await runtime.FileVisualizations.InspectSessionArtifactAsync(sessionId, ReadOptionalString(body, "snapshotId"), path, ReadOptionalBoolean(body, "forceText"), ReadOptionalBoolean(body, "allowLargeFile"), cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.NotFound, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "sessions" && segments[3] == "artifacts" && segments[4] == "query")
        {
            var sessionId = Uri.UnescapeDataString(segments[2]);
            var body = await ReadJsonAsync<JsonObject>(request, cancellationToken).ConfigureAwait(false);
            var path = ReadOptionalString(body, "path");
            var sql = ReadOptionalString(body, "sql");
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sql))
            {
                throw new InvalidDataException("An artifact database path and read-only SQL query are required.");
            }

            var result = await runtime.FileVisualizations.QuerySessionArtifactDatabaseAsync(sessionId, ReadOptionalString(body, "snapshotId"), path, sql, ReadOptionalInt32(body, "maxRows", 100), cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
