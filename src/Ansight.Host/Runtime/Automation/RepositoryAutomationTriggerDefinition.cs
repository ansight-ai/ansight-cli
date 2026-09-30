using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Automation;

internal sealed record RepositoryAutomationTriggerDefinition(
    string RepositoryRootPath,
    string TriggerId,
    int SchemaVersion,
    string AppId,
    string EventKind,
    JsonObject? EventSchema,
    RepositoryAutomationRegistration Automation,
    IReadOnlyList<RepositoryAutomationTriggerCondition> Conditions)
{
    public ExecutionRequirements? Requires { get; init; }

    public RepositoryAutomationTrigger ToPublicDefinition()
        => new(
            RepositoryRootPath,
            TriggerId,
            AppId,
            EventKind,
            Automation.AutomationId,
            Automation.ActionKindName,
            Automation.ActionTarget,
            Automation.FunctionTimeout,
            Automation.ActionTimeout,
            Automation.RetryPolicy.MaximumAttempts,
            Automation.RetryPolicy.InitialDelay,
            Automation.RetryPolicy.BackoffMultiplier,
            Automation.RetryPolicy.MaximumDelay)
        {
            SchemaVersion = this.SchemaVersion,
            Requires = Requires,
            Enabled = Enabled,
            ModulePath = ModulePath,
            EventSchema = this.EventSchema?.DeepClone().AsObject()
        };

    public string ModulePath { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;
}
