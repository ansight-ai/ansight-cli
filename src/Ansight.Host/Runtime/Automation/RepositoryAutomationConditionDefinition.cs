using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Automation;

internal sealed class RepositoryAutomationConditionDefinition
{
    public string? Field { get; init; }
    public string? Operator { get; init; }
    public JsonNode? Value { get; init; }
    public bool IgnoreCase { get; init; }
}
