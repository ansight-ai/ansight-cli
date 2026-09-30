namespace Ansight.Host.Runtime.BinaryTransfers;

public sealed record BinaryFileDownloadResult(
    string SessionId,
    string RequestId,
    string DownloadId,
    string TransferId,
    string RemoteFileName,
    string? RemoteFileExtension,
    string MimeType,
    long SizeBytes,
    string Version,
    string LocalFilePath,
    DateTimeOffset CompletedAtUtc);
