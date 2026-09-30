using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudTestRunUploadRequest(
    Guid TeamId,
    string AppId,
    string ClientRunId,
    string? TestId,
    string? TestName,
    string? WorkspaceName,
    string Model,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds,
    int InstructionCount,
    int ModelPassCount,
    int AnsightToolCallCount,
    int SuccessfulAnsightToolCallCount,
    long TotalTokens,
    int PassedCount,
    int FailedCount,
    int SkippedCount,
    string Summary)
{
    public string? AppVersion { get; init; }

    public string? SourceBranch { get; init; }

    public string? CommitSha { get; init; }

    public bool IsCi { get; init; }

    public bool IsDefinition { get; init; }

    public string? Framework { get; init; }

    public string? SessionId { get; init; }
}
