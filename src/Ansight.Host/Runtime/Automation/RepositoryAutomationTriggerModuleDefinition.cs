using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Automation;

internal sealed class RepositoryAutomationTriggerModuleDefinition
{
    public ExecutionRequirements? Requires { get; init; }

    public int? SchemaVersion { get; init; }

    public bool? Enabled { get; init; }

    public string? AppId { get; init; }

    public string? EventKind { get; init; }

    public JsonObject? EventSchema { get; init; }

    public int? FunctionTimeoutMs { get; init; }

    public int? ActionTimeoutSeconds { get; init; }

    public RepositoryAutomationRetryDefinition? Retry { get; init; }

    public List<RepositoryAutomationConditionDefinition> Conditions { get; init; } = [];

}

internal sealed class RepositoryAutomationRetryDefinition
{
    public int? MaxAttempts { get; init; }

    public int? InitialDelayMs { get; init; }

    public double? BackoffMultiplier { get; init; }

    public int? MaxDelayMs { get; init; }
}

internal sealed class RepositoryAutomationConditionDefinition
{
    public string? Field { get; init; }

    public string? Operator { get; init; }

    public JsonNode? Value { get; init; }

    public bool IgnoreCase { get; init; }
}
