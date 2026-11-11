using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using Ansight.RemoteSimulator.Core.AppInspection;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CliRemoteAppInspectionSource : IRemoteAppInspectionSource
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RuntimeCoordinator runtime;
    private readonly CliRemoteAnnotationSource annotations;

    public CliRemoteAppInspectionSource(RuntimeCoordinator runtime, CliRemoteAnnotationSource annotations)
    {
        this.runtime = runtime;
        this.annotations = annotations;
    }

    public async Task<RemoteAppInspectionResult> InvokeAsync(string deviceUdid, RemoteAppInspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var sessions = await annotations.ListLiveSessionsAsync(cancellationToken).ConfigureAwait(false);
        var matchesDevice = sessions.Any(session => session.SessionId == request.SessionId
            && string.Equals(session.DeviceUdid, deviceUdid, StringComparison.OrdinalIgnoreCase));

        if (!matchesDevice)
        {
            return RemoteAppInspectionResult.Failure(HttpStatusCode.NotFound, "The selected device no longer has this live app session.");
        }

        if (request.Operation is "catalog" or "tools.list")
        {
            var catalog = await runtime.AppTools.QueryForCompanionAsync(request.SessionId, request.RequestId, cancellationToken).ConfigureAwait(false);

            if (!catalog.Success)
            {
                return RemoteAppInspectionResult.Failure(HttpStatusCode.BadGateway, catalog.Message);
            }

            var tools = catalog.Envelope?.Payload?["tools"] as JsonArray ?? [];
            var operations = new List<string> { "tools.list", "artifacts.list", "artifacts.download" };
            var toolIDs = tools.OfType<JsonObject>()
                .Where(tool => tool["executable"]?.GetValue<bool>() != false)
                .Select(tool => tool["id"]?.GetValue<string>())
                .ToHashSet(StringComparer.Ordinal);

            if (toolIDs.Contains("files.list_directory") && toolIDs.Contains("files.download_file"))
            {
                operations.Add("files.list");
                operations.Add("files.download");
            }

            return Json(new { request.SessionId, operations, tools });
        }

        var snapshot = await runtime.Sessions.LoadSnapshotAsync(request.SessionId, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (snapshot is null)
        {
            return RemoteAppInspectionResult.Failure(HttpStatusCode.NotFound, "The session is no longer available.");
        }

        if (request.Operation == "artifacts.list")
        {
            return Json(snapshot.ArtifactSnapshots.OrderByDescending(artifact => artifact.CapturedAtUtc).Select(artifact => new
            {
                id = artifact.SnapshotId,
                artifact.Name,
                artifact.Kind,
                artifact.CapturedAtUtc,
                artifact.ByteCount,
                entries = artifact.Entries.Where(entry => entry.Kind != "directory").Select(entry => new
                {
                    entry.Name,
                    rootAlias = artifact.SnapshotId,
                    relativePath = entry.SnapshotRelativePath,
                    entry.Kind,
                    entry.SizeBytes,
                    entry.FileExtension,
                    entry.MimeType,
                    entry.LastModifiedUtc
                })
            }));
        }

        var arguments = JsonNode.Parse(request.Arguments.GetRawText()) as JsonObject ?? new JsonObject();

        if (request.Operation == "artifacts.download")
        {
            return await DownloadArtifactAsync(snapshot, arguments, cancellationToken).ConfigureAwait(false);
        }

        var toolId = request.Operation switch
        {
            "files.list" => "files.list_directory",
            "files.download" => "files.download_file",
            _ => null
        };

        if (toolId is null)
        {
            return RemoteAppInspectionResult.Failure(HttpStatusCode.BadRequest, "This file inspection operation is not supported.");
        }

        var response = await runtime.AppTools.CallForCompanionAsync(request.SessionId, toolId, arguments,
            request.RequestId, cancellationToken).ConfigureAwait(false);

        return ReadToolResult(response);
    }

    private static RemoteAppInspectionResult ReadToolResult(RuntimeAppToolResponse response)
    {
        if (!response.Success || response.Envelope?.Payload?["result"] is not JsonObject result)
        {
            return RemoteAppInspectionResult.Failure(HttpStatusCode.BadGateway, response.Message);
        }

        return Json(result);
    }

    private async Task<RemoteAppInspectionResult> DownloadArtifactAsync(AppSessionSnapshot session, JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var artifact = session.ArtifactSnapshots.FirstOrDefault(artifact => artifact.SnapshotId == arguments["root"]?.GetValue<string>());
        var entry = artifact?.Entries.FirstOrDefault(entry => entry.SnapshotRelativePath == arguments["path"]?.GetValue<string>());
        var canRead = artifact is not null
            && entry is not null
            && entry.Kind != "directory";

        if (!canRead || !SessionFileLocator.TryResolveArtifactEntryPath(runtime.ApplicationPaths, session, artifact!, entry!, out var path))
        {
            return RemoteAppInspectionResult.Failure(HttpStatusCode.NotFound, "The saved artifact file was not found.");
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var offset = arguments["offsetBytes"]?.GetValue<long>() ?? 0;
            var maximum = Math.Clamp(arguments["maxBytes"]?.GetValue<int>() ?? 262144, 1, 1048576);
            var version = $"{stream.Length}:{File.GetLastWriteTimeUtc(path).Ticks}";
            var expectedVersion = arguments["expectedVersion"]?.GetValue<string>();

            if (offset < 0 || offset > stream.Length)
            {
                return RemoteAppInspectionResult.Failure(HttpStatusCode.BadRequest, "The requested file offset is invalid.");
            }

            if (expectedVersion is not null && expectedVersion != version)
            {
                return RemoteAppInspectionResult.Failure(HttpStatusCode.Conflict, "The artifact changed during download. Try again.");
            }

            stream.Position = offset;
            var bytes = new byte[(int)Math.Min(maximum, stream.Length - offset)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);

            return Json(new
            {
                fileName = entry!.Name,
                entry.MimeType,
                sizeBytes = stream.Length,
                base64 = Convert.ToBase64String(bytes),
                bytesRead = bytes.Length,
                hasMore = stream.Position < stream.Length,
                nextOffsetBytes = stream.Position,
                version
            });
        }
        catch (IOException)
        {
            return RemoteAppInspectionResult.Failure(HttpStatusCode.NotFound, "The saved artifact file is no longer available.");
        }
    }

    private static RemoteAppInspectionResult Json<T>(T value)
    {
        return RemoteAppInspectionResult.Json(JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions));
    }
}
