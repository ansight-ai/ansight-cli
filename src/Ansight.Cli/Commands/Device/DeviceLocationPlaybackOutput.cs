namespace Ansight.Cli.Commands.Device;

internal sealed record DeviceLocationPlaybackOutput(
    string Schema,
    DeviceLocationPlaybackSnapshot Playback);
