namespace Ansight.Host.Workspaces.Execution;

/// <summary>Runner credentials scoped to one local CLI request, never stored in host settings.</summary>
public static class RunnerAiCredentialContext
{
    private static readonly AsyncLocal<RunnerAiCredentials?> current = new();
    public static RunnerAiCredentials? Current => current.Value;

    public static IDisposable Push(RunnerAiCredentials credentials)
    {
        var previous = current.Value;
        current.Value = credentials;
        return new Scope(() => current.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

public sealed record RunnerAiCredentials(string ApiKey, string? TeamId)
{
    public override string ToString() => $"Runner AI credentials for team {TeamId ?? "unspecified"}";
}
