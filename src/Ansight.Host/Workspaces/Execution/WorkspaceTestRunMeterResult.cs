namespace Ansight.Host.Workspaces.Execution;

public sealed record WorkspaceTestRunMeterResult(
    bool IsSuccess,
    string Message,
    SimulatorAgentRunCost? Cost = null)
{
    public static WorkspaceTestRunMeterResult Success(SimulatorAgentRunCost? cost = null) =>
        new(true, string.Empty, cost);

    public static WorkspaceTestRunMeterResult Failure(string message) =>
        new(
            false,
            string.IsNullOrWhiteSpace(message)
                ? "Workspace test metering failed."
                : message.Trim(),
            null);
}
