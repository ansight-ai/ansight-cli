namespace Ansight.Cli.Commands.Update;

internal sealed record CliInstallationReceipt(
    string Schema,
    string Version,
    long BuildNumber,
    string Channel,
    string Rid,
    string ReleaseUrl,
    string DownloadBaseUrl,
    string ArchiveUrl,
    string Sha256,
    DateTimeOffset InstalledAtUtc,
    string? InstallRoot,
    string? BinDirectory);
