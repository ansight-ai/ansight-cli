namespace Ansight.Host.Devices.Location;

public sealed record DeviceLocationPlaybackStartResult(
    bool IsSuccess,
    string Message,
    DeviceLocationPlaybackSnapshot Playback);
