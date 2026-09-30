namespace Ansight.Host.Workspaces.Targets;

public sealed record WorkspaceTestTargetRequest(
    string? Platform = null,
    string? DeviceIdentifier = null,
    string? ApplicationPath = null,
    string? DeviceKind = null,
    bool Headless = false)
{
    public string ExecutionMode { get; init; } = WorkspaceExecutionModes.Sdk;
}
