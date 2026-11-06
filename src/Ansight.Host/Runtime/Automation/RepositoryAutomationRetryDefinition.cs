using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Automation;

internal sealed class RepositoryAutomationRetryDefinition
{
    public int? MaxAttempts { get; init; }
    public int? InitialDelayMs { get; init; }
    public double? BackoffMultiplier { get; init; }
    public int? MaxDelayMs { get; init; }
}
