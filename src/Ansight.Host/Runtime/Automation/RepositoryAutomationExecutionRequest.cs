namespace Ansight.Host.Runtime.Automation;

internal sealed record RepositoryAutomationExecutionRequest(
    string RunId,
    RepositoryAutomationTriggerDefinition Trigger,
    AutomationEventEnvelope Event,
    DateTimeOffset EnqueuedAtUtc,
    int AttemptNumber = 1);
