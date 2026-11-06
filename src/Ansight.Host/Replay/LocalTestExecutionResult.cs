
namespace Ansight.Host.Replay;

public sealed record LocalTestExecutionResult(
    bool IsSuccess,
    string Message,
    string? TestId,
    string? SessionId,
    int PassedCount,
    int FailedCount,
    int SkippedCount,
    bool WasCancelled)
{
    public string? TraceRunId { get; init; }
    public string? TraceError { get; init; }
}
