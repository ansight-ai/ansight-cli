using Ansight.Infrastructure.Dependencies;

namespace Ansight.Cli.Commands.Update;

internal sealed record CliUpdateStatusOutput(
    string Schema,
    string CurrentVersion,
    long CurrentBuildNumber,
    string LatestVersion,
    long LatestBuildNumber,
    string Channel,
    string Rid,
    bool IsUpdateAvailable,
    bool IsVersionChange,
    bool IsChannelChange,
    string ReleaseUrl,
    string DownloadBaseUrl,
    string ArchiveUrl,
    string? Summary,
    DateTimeOffset? PublishedAtUtc);
