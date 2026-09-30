namespace Ansight.Host.Runtime.Operations;

// Trusted caller metadata, passed separately from user-controlled tool arguments.
internal sealed record OperationExecutionContext(
    string BatchRunId,
    string TestId,
    bool IsParallelTestBatch,
    Action<string>? Log = null);
