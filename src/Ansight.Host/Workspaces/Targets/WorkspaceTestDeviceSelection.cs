namespace Ansight.Host.Workspaces.Targets;

internal sealed record WorkspaceTestDeviceSelection(
    bool IsSuccess,
    string Message,
    DeviceDescriptor? Device = null)
{
    public static WorkspaceTestDeviceSelection Success(DeviceDescriptor device)
        => new(true, string.Empty, device);

    public static WorkspaceTestDeviceSelection Failure(string message)
        => new(false, message);
}
