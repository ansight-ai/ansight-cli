using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Workspaces.Execution;

internal static class WorkspaceTestPreTaskRunner
{
    internal static async Task<IReadOnlyList<RepositoryTaskRunResult>> RunAsync(
        IReadOnlyList<WorkspaceTestPreTask> preTasks,
        Func<WorkspaceTestPreTask, CancellationToken, Task<RepositoryTaskRunResult>> execute,
        CancellationToken cancellationToken)
    {
        var results = new List<RepositoryTaskRunResult>();

        foreach (var preTask in preTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await execute(preTask, cancellationToken).ConfigureAwait(false);
            results.Add(result);

            if (result.Status != RepositoryTaskRunStatus.Passed)
            {
                break;
            }
        }

        return results;
    }
}
