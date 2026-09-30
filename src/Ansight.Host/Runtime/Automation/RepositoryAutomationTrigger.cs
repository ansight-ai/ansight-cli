using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Automation;

/// <summary>
/// Read-only description of a repository trigger registered by the host.
/// </summary>
public sealed record RepositoryAutomationTrigger(
    string RepositoryRootPath,
    string TriggerId,
    string AppId,
    string EventKind,
    string AutomationId,
    string ActionKind,
    string ActionTarget,
    TimeSpan FunctionTimeout,
    TimeSpan ActionTimeout,
    int MaximumAttempts,
    TimeSpan InitialRetryDelay,
    double RetryBackoffMultiplier,
    TimeSpan MaximumRetryDelay)
{
    public ExecutionRequirements? Requires { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public bool Enabled { get; init; } = true;

    public string ModulePath { get; init; } = string.Empty;

    public JsonObject? EventSchema { get; init; }
}
