
namespace Ansight.Host.Replay;

public sealed record LocalTestExecutionSnapshot(
    string Schema,
    string ExecutionId,
    string Kind,
    string Status,
    string Message,
    string WorkspacePath,
    IReadOnlyList<string> TestIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<LocalTestExecutionProgress> Progress,
    LocalTestExecutionResult? Result);
