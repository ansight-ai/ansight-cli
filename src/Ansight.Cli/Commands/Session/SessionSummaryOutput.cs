namespace Ansight.Cli.Commands.Session;

internal sealed record SessionSummaryOutput(
    string SessionId,
    string AppId,
    string? Name,
    string ClientName,
    bool IsConnected,
    bool IsHistorical,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastUpdatedUtc,
    bool IsPinned,
    long CacheSizeBytes,
    IReadOnlyList<string> Tags)
{
    public string? Platform { get; init; }

    public string? DeviceFormFactor { get; init; }

    public string? OsName { get; init; }

    public string? OsVersion { get; init; }

    public bool? IsVirtual { get; init; }

    public bool? IsEmulator { get; init; }
}
