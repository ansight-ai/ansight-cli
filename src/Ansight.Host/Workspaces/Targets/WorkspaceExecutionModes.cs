namespace Ansight.Host.Workspaces.Targets;

public static class WorkspaceExecutionModes
{
    public const string Sdk = "sdk";
    public const string Device = "device";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or Sdk => Sdk,
        Device => Device,
        _ => throw new ArgumentException("Execution mode must be sdk or device.", nameof(value))
    };
}
