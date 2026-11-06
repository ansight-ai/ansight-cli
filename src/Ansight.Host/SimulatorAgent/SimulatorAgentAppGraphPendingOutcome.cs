using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAppGraphPendingOutcome(
    string InputId,
    string ActionId,
    string FromDestinationId,
    string ToolName,
    string EvidenceId)
{
    public string SemanticMeaning { get; init; } = string.Empty;

    public JsonObject Selector { get; init; } = new();

    internal JsonObject ToJson() => new()
    {
        ["inputId"] = InputId,
        ["actionId"] = ActionId,
        ["fromDestinationId"] = FromDestinationId,
        ["toolName"] = ToolName,
        ["evidenceId"] = EvidenceId,
        ["semanticMeaning"] = SemanticMeaning,
        ["selector"] = Selector.DeepClone()
    };
}
