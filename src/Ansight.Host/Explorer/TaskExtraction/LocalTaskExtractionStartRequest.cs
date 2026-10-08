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

public sealed record LocalTaskExtractionStartRequest(
    string SessionId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Description,
    string? TaskName = null,
    string Mode = LocalTaskExtractionModes.Hosted,
    string Model = "",
    Guid? TeamId = null,
    bool ValidateSelectors = true)
{
    public string Reasoning { get; init; } = AgentReasoningModes.Fast;
    public string? ReplaceExtractionId { get; init; }
    public bool TrimToTechnology { get; init; } = true;
    public bool IncludeOnlyNecessaryFeatures { get; init; } = true;
}
