namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentTokenUsage(
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    int CachedInputTokens,
    int CacheWriteInputTokens,
    int ReasoningOutputTokens)
{
    public static SimulatorAgentTokenUsage Empty { get; } = new(0, 0, 0, 0, 0, 0);

    public SimulatorAgentTokenUsage Add(SimulatorAgentTokenUsage other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new SimulatorAgentTokenUsage(
            InputTokens + other.InputTokens,
            OutputTokens + other.OutputTokens,
            TotalTokens + other.TotalTokens,
            CachedInputTokens + other.CachedInputTokens,
            CacheWriteInputTokens + other.CacheWriteInputTokens,
            ReasoningOutputTokens + other.ReasoningOutputTokens);
    }
}
