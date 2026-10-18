namespace Ansight.Cli.HostConnection;

internal sealed record ControlRequest(
    string Schema,
    string[] Arguments,
    string? SecretValue,
    bool StreamOutput = false,
    IReadOnlyDictionary<string, string>? SecretValues = null,
    string? CallerUserId = null,
    Ansight.Host.Workspaces.Execution.RunnerAiCredentials? RunnerAiCredentials = null,
    int? ExpectedProcessId = null,
    DateTimeOffset? ExpectedProcessStartedUtc = null);
