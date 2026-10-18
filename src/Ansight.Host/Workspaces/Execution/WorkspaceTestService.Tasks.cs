using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Workspaces.Execution;

public sealed partial class WorkspaceTestService
{
    private async Task<WorkspaceTestRunResult> RunTaskOnlyAsync(
        WorkspaceTestDefinition test, WorkspaceTestRunRequest request, string workspacePath,
        AppSessionSnapshot session, WorkspaceTestTarget? target, string runId,
        IReadOnlyList<SimulatorAgentStartupStep> startupSteps,
        IProgress<WorkspaceTestRunProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var secrets = simulatorAgent.ResolveTestSecrets(test.AppId, test.RequiredSecrets, request.SecretResolver);
            using var trace = RepositoryTaskTraceScope.Begin(request.CaptureTrace);
            progress?.Report(new WorkspaceTestRunProgress("task.running", $"Running repository task '{test.TaskId}'."));
            var taskResult = await runtime.RunWorkspaceTestTaskAsync(
                RepositoryTaskWorkspaceScope.Resolve(test.AppId, workspacePath), test.AppId,
                session.SessionId, test.TaskId!, test.TaskInput, target?.DeviceIdentifier,
                request.OperationContext, secrets, cancellationToken, runId, request.BatchRunId).ConfigureAwait(false);
            var status = taskResult.Status switch
            {
                RepositoryTaskRunStatus.Passed => SimulatorAgentRunStatus.Succeeded,
                RepositoryTaskRunStatus.Cancelled => SimulatorAgentRunStatus.Cancelled,
                _ => SimulatorAgentRunStatus.Failed
            };
            // Reuse test history and trace export without constructing an agent result or
            // inventing instructions, model passes, tokens, or model tool calls.
            var audit = new SimulatorAgentRunAudit(
                SchemaVersion: 1, RunId: taskResult.RunId, SessionId: session.SessionId,
                Model: string.Empty, Status: status, Message: taskResult.Message,
                StartedUtc: taskResult.StartedAtUtc, CompletedUtc: taskResult.CompletedAtUtc,
                DurationMilliseconds: taskResult.DurationMilliseconds,
                MaximumTurnsPerInstruction: 0, MaximumToolCalls: 0,
                InstructionCount: 0, PassedInstructionCount: 0, FailedInstructionCount: 0,
                CancelledInstructionCount: 0, ModelPassCount: 0, FailedModelPassCount: 0,
                FunctionCallCount: 0, AnsightToolCallCount: taskResult.ToolCalls.Count,
                SuccessfulAnsightToolCallCount: taskResult.ToolCalls.Count(call => !call.IsError),
                FailedAnsightToolCallCount: taskResult.ToolCalls.Count(call => call.IsError),
                Tokens: SimulatorAgentTokenUsage.Empty, RequestedInstructions: [],
                Instructions: [], ModelPasses: [], ToolCalls: [])
            {
                ExecutionKind = "task",
                TraceId = taskResult.TraceId,
                OperationId = taskResult.OperationId,
                TaskResult = taskResult,
                AppId = test.AppId,
                ExecutionMode = session.CaptureSource == WorkspaceExecutionModes.Device
                    ? WorkspaceExecutionModes.Device : WorkspaceExecutionModes.Sdk,
                WorkspacePath = workspacePath,
                WorkspaceTestId = test.TestId,
                WorkspaceTestName = test.Name,
                BatchRunId = request.BatchRunId,
                StartupSteps = startupSteps,
                TraceEnabled = request.CaptureTrace
            };
            var persistence = simulatorAgent.PersistRunAudit(audit);
            progress?.Report(new WorkspaceTestRunProgress("task.completed", taskResult.Message));
            return new WorkspaceTestRunResult(status == SimulatorAgentRunStatus.Succeeded,
                taskResult.Message, test, session.SessionId, null, [], target)
            {
                TaskResult = taskResult,
                TaskAudit = audit,
                TaskAuditFilePath = persistence.FilePath,
                TaskAuditPersistenceError = persistence.ErrorMessage,
                AppiumSessionId = target is null ? null
                    : runtime.GetAppiumSessionId(target.DeviceIdentifier, target.ApplicationIdentifier)
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                         or IOException or UnauthorizedAccessException)
        {
            return WorkspaceTestRunResult.Failure(exception.Message, test, session.SessionId, target: target);
        }
    }
}
