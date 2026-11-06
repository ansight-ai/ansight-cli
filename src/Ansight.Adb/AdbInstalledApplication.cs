namespace Ansight.Adb;

public sealed record AdbInstalledApplication(
    string PackageIdentifier,
    string PackagePath,
    string? Version,
    string? BuildVersion,
    DateTimeOffset? InstalledAtUtc,
    DateTimeOffset? LastUpdatedAtUtc);
