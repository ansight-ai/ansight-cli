namespace Ansight.Host.Workspaces.Execution;

internal sealed record WorkspaceTestMeterStartResult(
    bool IsSuccess,
    string Message,
    int? MaximumRoundTrips)
{
    public static WorkspaceTestMeterStartResult Success(int? maximumRoundTrips) =>
        new(true, string.Empty, maximumRoundTrips);

    public static WorkspaceTestMeterStartResult Failure(string message) =>
        new(
            false,
            string.IsNullOrWhiteSpace(message)
                ? "Workspace test metering failed."
                : message.Trim(),
            null);
}
