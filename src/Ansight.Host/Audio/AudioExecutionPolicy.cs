using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Audio;

internal static class AudioExecutionPolicy
{
    internal const string ParallelErrorCode = "audio-parallel-execution-unsupported";
    public static RequestResult? RejectParallelCall(string toolName, OperationExecutionContext? context)
    {
        var batch = context;
        if (batch?.IsParallelTestBatch != true || toolName is not ("ansight_get_audio_capabilities" or "ansight_inject_audio"))
            return null;

        var message = $"{ParallelErrorCode}: {toolName} is unavailable in parallel test execution. "
            + "Run the complete audio test serially on one selected device without --parallel; "
            + "multiple selected devices also run concurrently in matrix mode.";
        batch.Log?.Invoke($"{message} Batch: {batch.BatchRunId}; test: {batch.TestId}.");
        return RequestResult.ToolResult(new JsonObject
        {
            ["schema"] = toolName == "ansight_inject_audio" ? "ansight.audio-injection/v1" : "ansight.audio-capabilities/v1",
            ["status"] = "failed",
            ["available"] = false,
            ["code"] = ParallelErrorCode,
            ["message"] = message,
            ["diagnostics"] = new JsonObject
            {
                ["deliveryStarted"] = false,
                ["batchRunId"] = batch.BatchRunId,
                ["testId"] = batch.TestId
            }
        }, isError: true);
    }

}
