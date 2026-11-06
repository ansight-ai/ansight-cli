namespace Ansight.Host.Workspaces.Execution;

internal sealed record TestVideoRecordingRequest(
    string Platform, string DeviceIdentifier, bool IsVirtual, string OutputPath);
