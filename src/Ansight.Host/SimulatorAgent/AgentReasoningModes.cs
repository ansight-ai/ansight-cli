namespace Ansight.Host.SimulatorAgent;

public static class AgentReasoningModes
{
    public const string Fast = "fast";
    public const string Balanced = "balanced";
    public const string Deep = "deep";

    public static string Normalize(string? reasoning) => reasoning?.Trim().ToLowerInvariant() switch
    {
        null or "" => Fast,
        Fast => Fast,
        Balanced => Balanced,
        Deep => Deep,
        _ => throw new ArgumentException("Reasoning must be fast, balanced, or deep.", nameof(reasoning))
    };
}
