namespace Ansight.Host.Workspaces.Authoring;

public sealed record WorkspaceTriggerCreateRequest(
    string WorkspacePath,
    string TriggerId,
    string EventKind = "app.event",
    string? AppId = null,
    bool Overwrite = false);
