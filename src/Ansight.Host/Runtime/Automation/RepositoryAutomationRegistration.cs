using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Automation;

internal enum RepositoryAutomationActionKind
{
    TypeScript,
    AppTool
}

internal sealed record RepositoryAutomationRegistration(
    string RepositoryRootPath,
    string AutomationId,
    RepositoryAutomationActionKind ActionKind,
    string? EntrypointPath,
    string? AppToolId,
    JsonObject? AppToolArguments,
    TimeSpan FunctionTimeout,
    TimeSpan ActionTimeout,
    RepositoryAutomationRetryPolicy RetryPolicy)
{
    public string ActionKindName => ActionKind switch
    {
        RepositoryAutomationActionKind.TypeScript => "typescript",
        RepositoryAutomationActionKind.AppTool => "appTool",
        _ => throw new InvalidOperationException($"Unsupported repository automation action kind '{ActionKind}'.")
    };

    public string ActionTarget => ActionKind switch
    {
        RepositoryAutomationActionKind.TypeScript => EntrypointPath
                                                     ?? throw new InvalidOperationException("TypeScript entrypoint is missing."),
        RepositoryAutomationActionKind.AppTool => AppToolId
                                                  ?? throw new InvalidOperationException("App tool id is missing."),
        _ => throw new InvalidOperationException($"Unsupported repository automation action kind '{ActionKind}'.")
    };
}
