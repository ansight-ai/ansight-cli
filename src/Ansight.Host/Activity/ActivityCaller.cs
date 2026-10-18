namespace Ansight.Host.Activity;

/// <summary>Credential ownership and the program executing an operation are separate identities.</summary>
public sealed record ActivityCaller
{
    public string? PrincipalId { get; init; }
    public string PrincipalAttribution { get; init; } = "unknown";
    public string Kind { get; init; } = "unknown";
    public string? Name { get; init; }
    public string ExecutorAttribution { get; init; } = "unknown";
    public string Ingress { get; init; } = "host";
    public string? ClientName { get; init; }
    public string? ClientVersion { get; init; }
    public string? WorkId { get; init; }
    public string? InitiatorKind { get; init; }
    public string? InitiatorName { get; init; }
    public string? InitiatorAttribution { get; init; }

    public static ActivityCaller FromEnvironment(string ingress = "cli") => Declared(new ActivityCaller
    {
        Kind = Environment.GetEnvironmentVariable("ANSIGHT_CALLER_KIND") ?? "unknown",
        Name = Environment.GetEnvironmentVariable("ANSIGHT_CALLER_NAME"),
        WorkId = Environment.GetEnvironmentVariable("ANSIGHT_WORK_ID"),
        ClientName = "ansight-cli",
        ClientVersion = typeof(ActivityCaller).Assembly.GetName().Version?.ToString(),
        Ingress = ingress
    });

    /// <summary>Untrusted IPC metadata must never assert an authenticated principal or a host executor.</summary>
    public static ActivityCaller Declared(ActivityCaller? caller) => new()
    {
        Kind = caller?.Kind is "human" or "agent" or "automation" ? caller.Kind : "unknown",
        Name = Limit(caller?.Name),
        ExecutorAttribution = caller?.Kind is "human" or "agent" or "automation" || !string.IsNullOrWhiteSpace(caller?.Name)
            ? "caller-declared" : "unknown",
        Ingress = caller?.Ingress is "cli" or "mcp" or "player" or "runner" ? caller.Ingress : "cli",
        ClientName = Limit(caller?.ClientName),
        ClientVersion = Limit(caller?.ClientVersion),
        WorkId = Limit(caller?.WorkId)
    };

    internal static string? Limit(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, 256)];
}
