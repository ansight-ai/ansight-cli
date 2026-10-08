using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using Ansight.Host.Runtime.Sanitization;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.SimulatorAgent;
using Ansight.Host.Workspaces.Execution;
using Ansight.Tools;

namespace Ansight.Host.Replay;

public sealed record LocalTaskExtractionSnapshot(
    string Schema,
    string ExtractionId,
    string Status,
    string Message,
    string SessionId,
    string AppId,
    string WorkspacePath,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string TaskName,
    string Description,
    string Mode,
    string Model,
    bool ValidateSelectors,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<LocalTaskExtractionProgress> Progress,
    LocalTaskExtractionDraft? Draft,
    string TestStatus,
    string? TestMessage,
    LocalTaskExtractionTestResult? TestResult,
    string? CommittedPath,
    LocalTaskExtractionTrace? Trace)
{
    public string Reasoning { get; init; } = AgentReasoningModes.Fast;

    public string? ReasoningEffort { get; init; }

    public string? ReasoningConfigurationRevision { get; init; }

    public bool TaskNameIsAuthoritative { get; init; } = true;
    public bool TrimToTechnology { get; init; } = true;
    public bool IncludeOnlyNecessaryFeatures { get; init; } = true;
    public IReadOnlyList<string>? AutomationIds { get; init; }
}
