namespace Ansight.Host.Runtime.BinaryTransfers;

public sealed class BinaryFileDownloadOptions
{
    public string? RequestId { get; init; }

    public required string SessionId { get; init; }

    public string? SandboxRoot { get; init; }

    public required string SandboxPath { get; init; }

    public required string DestinationDirectoryPath { get; init; }

    public string? DownloadId { get; init; }

    public int ChunkBytes { get; init; } = 64 * 1024;
}
