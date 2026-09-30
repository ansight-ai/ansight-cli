namespace Ansight.Host.Devices.Location;

public sealed record DeviceLocationRoute(
    string SourceFileName,
    IReadOnlyList<DeviceLocationPoint> Points,
    double DistanceMeters,
    TimeSpan? RecordedDuration,
    bool HasRecordedTiming);
