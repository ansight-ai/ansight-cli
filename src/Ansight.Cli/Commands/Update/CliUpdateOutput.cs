namespace Ansight.Cli.Commands.Update;

internal sealed record CliVersionOutput(
    string Schema,
    string Version,
    long BuildNumber,
    string InformationalVersion,
    string CommitSha,
    string Channel,
    string Rid,
    string DataDirectory,
    string ExecutablePath,
    bool IsInstallerManaged,
    string? ReceiptPath,
    string? UpdateFeed);

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

internal sealed record CliUpdateAppliedOutput(
    string Schema,
    string PreviousVersion,
    long PreviousBuildNumber,
    string InstalledVersion,
    long InstalledBuildNumber,
    string Channel,
    bool WasUpdated,
    bool ChannelChanged,
    string Message);
