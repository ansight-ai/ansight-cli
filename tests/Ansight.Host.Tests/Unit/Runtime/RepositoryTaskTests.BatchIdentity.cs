using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData("batch-explicit", null, false)]
    [InlineData(null, "batch-context", false)]
    [InlineData(null, "batch-parallel", true)]
    public async Task Router_PreservesBatchIdentityInChildTasks(
        string? batchRunId, string? contextBatchRunId, bool isParallelTestBatch)
    {
        using var repository = CreateRepository("""
            export const task = {
              "schemaVersion": 1, "appId": "com.example.app",
              "title": "Parent", "description": "Calls a child within the same batch."
            };
            export default async ({ ansight, run, expect }) => {
              const child = await ansight.tasks.run({ taskId: "map.child" });
              expect(child.output.batchRunId, { id: "same-batch" }).toBe(run.batchRunId);
              expect(child.output.runId === run.runId, { id: "distinct-runs" }).toBe(false);
              return {
                batchRunId: run.batchRunId,
                hasBatchRunId: Object.hasOwn(run, "batchRunId"),
                child: child.output
              };
            };
            """);
        File.WriteAllText(Path.Combine(repository.RootPath, "ansight", "tasks", "map", "child.ts"), """
            export const task = {
              "schemaVersion": 1, "appId": "com.example.app",
              "title": "Child", "description": "Returns its host-owned run identity."
            };
            export default async ({ run, expect }) => {
              expect(Object.isFrozen(run), { id: "readonly-run" }).toBe(true);
              return {
                runId: run.runId,
                batchRunId: run.batchRunId,
                hasBatchRunId: Object.hasOwn(run, "batchRunId")
              };
            };
            """);
        var router = CreateConfiguredRouter(repository.RootPath);
        var task = Assert.Single(router.Load(repository.RootPath, "com.example.app").Tasks,
            candidate => candidate.TaskId == "map.validate");
        var context = contextBatchRunId is null
            ? null
            : new OperationExecutionContext(contextBatchRunId, "test", isParallelTestBatch);

        var result = await router.ExecuteAsync(task, "session-batch", new JsonObject(),
            "batch-test", CancellationToken.None, context: context, batchRunId: batchRunId);

        Assert.True(result.Status == RepositoryTaskRunStatus.Passed, result.Message);
        var expectedBatchRunId = batchRunId ?? contextBatchRunId;
        Assert.Equal(expectedBatchRunId, result.Output?["batchRunId"]?.GetValue<string>());
        Assert.Equal(expectedBatchRunId, result.Output?["child"]?["batchRunId"]?.GetValue<string>());
        Assert.Equal(expectedBatchRunId is not null, result.Output?["hasBatchRunId"]?.GetValue<bool>());
        Assert.Equal(expectedBatchRunId is not null, result.Output?["child"]?["hasBatchRunId"]?.GetValue<bool>());
    }
}
