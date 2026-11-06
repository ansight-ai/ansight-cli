namespace Ansight.Host.Workspaces.Execution;

public sealed record RunnerAiCredentials(string ApiKey, string? TeamId)
{
    public override string ToString() => $"Runner AI credentials for team {TeamId ?? "unspecified"}";
}
