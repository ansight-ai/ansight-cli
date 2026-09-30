namespace Ansight.Cli.Commands.Device;

internal sealed record DeviceLocationPlaybackOperationOutput(
    string Schema,
    OperationResult Result,
    DeviceLocationPlaybackSnapshot Playback);
