namespace Ansight.Host.Workspaces.Targets;

public sealed record WorkspaceTestTarget(
    string Platform,
    string DeviceIdentifier,
    string DeviceName,
    string ApplicationIdentifier,
    bool DeviceStarted,
    bool ApplicationInstalled,
    bool ApplicationLaunched)
{
    public string ExecutionMode { get; init; } = WorkspaceExecutionModes.Sdk;

    public string DeviceKind { get; init; } = DeviceKinds.Unknown;
}
