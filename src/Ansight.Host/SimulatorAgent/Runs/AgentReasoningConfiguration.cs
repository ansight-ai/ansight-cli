namespace Ansight.Host.SimulatorAgent;

public sealed record AgentReasoningConfiguration(
    string Reasoning,
    string Model,
    string ReasoningEffort,
    string Revision)
{
    // Hosted runs must resolve their configuration on the server. These defaults
    // support the internal local runner when no hosted gateway is installed.
    public static AgentReasoningConfiguration CreateDefault(string? reasoning = null, string? modelOverride = null)
    {
        var mode = AgentReasoningModes.Normalize(reasoning);
        var model = !string.IsNullOrWhiteSpace(modelOverride)
            ? modelOverride.Trim()
            : mode switch
            {
                AgentReasoningModes.Fast => "gpt-5.6-luna",
                AgentReasoningModes.Balanced => "gpt-5.6-terra",
                _ => "gpt-5.6-sol"
            };
        return new(mode, model, "medium", "defaults-v1");
    }

    public static string NormalizeReasoningEffort(string effort) => effort?.Trim().ToLowerInvariant() switch
    {
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        "xhigh" => "xhigh",
        _ => throw new ArgumentException("Provider reasoning effort must be low, medium, high, or xhigh.", nameof(effort))
    };
}
