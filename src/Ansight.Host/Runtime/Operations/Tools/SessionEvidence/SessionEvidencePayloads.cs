using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal static class SessionEvidencePayloads
{
    public static JsonObject BuildScreenshotFramePayload(
        AppSessionSnapshot session,
        SessionImageFrame frame,
        string? localPath = null,
        long? fileSizeBytes = null)
    {
        var payload = new JsonObject
        {
            ["frameId"] = frame.FrameId,
            ["sessionId"] = session.SessionId,
            ["appId"] = session.AppId,
            ["capturedAtUtc"] = frame.CapturedAtUtc,
            ["format"] = frame.Format,
            ["mimeType"] = ResolveImageMimeType(frame.Format),
            ["width"] = frame.Width,
            ["height"] = frame.Height,
            ["quality"] = frame.Quality,
            ["byteCount"] = frame.ByteCount
        };

        if (!string.IsNullOrWhiteSpace(localPath))
        {
            payload["localPath"] = localPath;
        }

        if (fileSizeBytes.HasValue)
        {
            payload["fileSizeBytes"] = fileSizeBytes.Value;
        }

        return payload;
    }

    public static JsonObject BuildArtifactSnapshotPayload(SessionArtifactSnapshot snapshot)
    {
        return new JsonObject
        {
            ["snapshotId"] = snapshot.SnapshotId,
            ["capturedAtUtc"] = snapshot.CapturedAtUtc,
            ["source"] = snapshot.Source,
            ["rootAlias"] = snapshot.RootAlias,
            ["relativePath"] = snapshot.RelativePath,
            ["name"] = snapshot.Name,
            ["kind"] = snapshot.Kind,
            ["artifactDirectoryName"] = snapshot.ArtifactDirectoryName,
            ["directoryCount"] = snapshot.DirectoryCount,
            ["fileCount"] = snapshot.FileCount,
            ["byteCount"] = snapshot.ByteCount,
            ["truncated"] = snapshot.Truncated
        };
    }

    public static JsonObject BuildArtifactEntryPayload(
        SessionArtifactSnapshot snapshot,
        SessionArtifactEntry entry,
        string? localPath = null)
    {
        var payload = new JsonObject
        {
            ["snapshotId"] = snapshot.SnapshotId,
            ["snapshotCapturedAtUtc"] = snapshot.CapturedAtUtc,
            ["name"] = entry.Name,
            ["rootAlias"] = entry.RootAlias,
            ["relativePath"] = entry.RelativePath,
            ["snapshotRelativePath"] = entry.SnapshotRelativePath,
            ["archiveRelativePath"] = entry.ArchiveRelativePath,
            ["kind"] = entry.Kind,
            ["sizeBytes"] = entry.SizeBytes,
            ["fileExtension"] = entry.FileExtension,
            ["mimeType"] = entry.MimeType,
            ["lastModifiedUtc"] = entry.LastModifiedUtc,
            ["isDirectory"] = IsArtifactDirectory(entry)
        };

        if (!string.IsNullOrWhiteSpace(localPath))
        {
            payload["localPath"] = localPath;
        }

        return payload;
    }

    public static JsonObject BuildMetricChannelPayload(SessionMetricChannel channel)
    {
        return new JsonObject
        {
            ["channelId"] = channel.ChannelId,
            ["name"] = channel.Name,
            ["type"] = MetricChannelClassification.ResolveTelemetryType(channel.ChannelId, channel),
            ["unit"] = channel.Unit,
            ["colorHex"] = channel.ColorHex,
            ["source"] = channel.Source,
            ["group"] = channel.Group,
            ["kind"] = channel.Kind
        };
    }

    public static JsonObject BuildMetricSamplePayload(
        SessionMetricSample sample,
        IReadOnlyDictionary<byte, SessionMetricChannel> channelMap)
    {
        channelMap.TryGetValue(sample.ChannelId, out var channel);
        return new JsonObject
        {
            ["capturedAtUtc"] = sample.CapturedAtUtc,
            ["channelId"] = sample.ChannelId,
            ["channelName"] = channel?.Name,
            ["channelSource"] = channel?.Source,
            ["channelGroup"] = channel?.Group,
            ["channelKind"] = channel?.Kind,
            ["type"] = PayloadJson.ResolveTelemetryType(sample.ChannelId, channelMap),
            ["value"] = sample.Value
        };
    }

    public static bool IsArtifactDirectory(SessionArtifactEntry entry)
        => string.Equals(entry.Kind, "directory", StringComparison.OrdinalIgnoreCase);

    public static string ResolveImageMimeType(string? format)
    {
        return format?.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }
}
