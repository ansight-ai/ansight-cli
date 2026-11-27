namespace Ansight.Host.Devices;

public sealed record DeviceUsageHistory(
    DateTimeOffset? LastBootedUtc = null,
    DateTimeOffset? LastUsedUtc = null);
