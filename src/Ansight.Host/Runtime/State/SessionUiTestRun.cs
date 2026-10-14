namespace Ansight.Host.Runtime.State;

// The host owns this custom-property group; SDK property updates must retain it.
internal sealed record SessionUiTestRun(
    string TestId,
    string TestName,
    string RunId,
    string Status,
    string ExecutionKind,
    DateTimeOffset StartedUtc,
    string? BatchRunId = null,
    int? BatchIndex = null,
    int? BatchCount = null,
    DateTimeOffset? CompletedUtc = null)
{
    internal const string PropertyGroup = "ui-test";
}
