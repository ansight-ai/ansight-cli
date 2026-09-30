namespace Ansight.Host.Devices.Location;

public sealed record DeviceLocationPlaybackSnapshot(
    string? RunId,
    bool IsPlaying,
    string Status,
    string Message,
    string? Platform,
    string? DeviceIdentifier,
    string? SourceFileName,
    int PointCount,
    int CurrentPointIndex,
    double DistanceMeters,
    TimeSpan? RecordedDuration,
    DeviceLocationPlaybackMode Mode,
    double PlaybackSpeedMultiplier,
    double FixedSpeedKph,
    bool Loop,
    DeviceLocationPoint? CurrentLocation,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc);
