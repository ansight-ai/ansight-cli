namespace Ansight.Host.Devices;

public sealed record InstalledApplication(
    string Identifier,
    string Name,
    string? BundlePath = null,
    string? Version = null,
    string? BuildVersion = null,
    DateTimeOffset? InstalledAtUtc = null,
    DateTimeOffset? LastUpdatedAtUtc = null);
