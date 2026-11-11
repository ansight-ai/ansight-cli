using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Workspaces.Execution;

public sealed partial class WorkspaceTestService
{
    private async Task<IReadOnlyList<RepositoryTaskRunResult>> RunPreTasksAsync(
        WorkspaceTestDefinition test, WorkspaceTestRunRequest request, string workspacePath,
        AppSessionSnapshot session, WorkspaceTestTarget? target, string runId,
        IProgress<WorkspaceTestRunProgress>? progress, CancellationToken cancellationToken)
    {
        var secrets = simulatorAgent.ResolveTestSecrets(test.AppId, test.RequiredSecrets, request.SecretResolver);
        using var trace = RepositoryTaskTraceScope.Begin(request.CaptureTrace);

        return await WorkspaceTestPreTaskRunner.RunAsync(test.PreTasks, async (preTask, token) =>
        {
            progress?.Report(new WorkspaceTestRunProgress("pre-task.running", $"Running pre-task '{preTask.TaskId}'."));
            var result = await runtime.RunWorkspaceTestTaskAsync(
                RepositoryTaskWorkspaceScope.Resolve(test.AppId, workspacePath), test.AppId,
                session.SessionId, preTask.TaskId, preTask.Input, target?.DeviceIdentifier,
                request.OperationContext, secrets, token, runId, request.BatchRunId).ConfigureAwait(false);
            progress?.Report(new WorkspaceTestRunProgress("pre-task.completed", $"Pre-task '{preTask.TaskId}': {result.Message}"));
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    private WorkspaceTestRunResult AttachPreTaskResults(
        WorkspaceTestRunResult result, IReadOnlyList<RepositoryTaskRunResult> preTaskResults)
    {
        if (preTaskResults.Count == 0)
        {
            return result;
        }

        result = result with { PreTaskResults = preTaskResults };

        if (result.RunAudit is not { } audit)
        {
            return result;
        }

        var updated = audit with { PreTaskResults = preTaskResults };
        var saved = simulatorAgent.PersistRunAudit(updated);

        if (result.AgentResult is { } agent)
        {
            return result with
            {
                AgentResult = agent with
                {
                    Audit = updated,
                    AuditFilePath = saved.FilePath ?? agent.AuditFilePath,
                    AuditPersistenceError = saved.ErrorMessage ?? agent.AuditPersistenceError
                }
            };
        }

        return result with
        {
            TaskAudit = updated,
            TaskAuditFilePath = saved.FilePath ?? result.TaskAuditFilePath,
            TaskAuditPersistenceError = saved.ErrorMessage ?? result.TaskAuditPersistenceError
        };
    }
}
