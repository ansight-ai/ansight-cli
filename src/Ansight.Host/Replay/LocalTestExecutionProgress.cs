
namespace Ansight.Host.Replay;

public sealed record LocalTestExecutionProgress(
    string Stage,
    string Message,
    DateTimeOffset OccurredAtUtc,
    int? TestIndex,
    int? TestCount,
    string? TestId);
