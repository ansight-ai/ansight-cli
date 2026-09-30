namespace Ansight.Host.Devices.Location;

public sealed record DeviceLocationPlaybackRequest(
    string Platform,
    string DeviceIdentifier,
    string SourceFileName,
    string RouteContent,
    DeviceLocationPlaybackMode Mode = DeviceLocationPlaybackMode.RecordedTiming,
    double PlaybackSpeedMultiplier = 1d,
    double FixedSpeedKph = 30d,
    bool Loop = false);
