using System.Globalization;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentWebSocketOptions(
    int CompactThresholdTokens = 32_000,
    bool EnableWarmup = false)
{
    internal const string CompactThresholdEnvironmentVariable = "ANSIGHT_OPENAI_COMPACT_THRESHOLD_TOKENS";
    internal const string WarmupEnvironmentVariable = "ANSIGHT_OPENAI_WEBSOCKET_WARMUP";

    internal static SimulatorAgentWebSocketOptions Resolve(
        SimulatorAgentWebSocketOptions? configured,
        Func<string, string?> readEnvironment)
    {
        var options = configured ?? new SimulatorAgentWebSocketOptions();
        if (configured is null)
        {
            var threshold = readEnvironment(CompactThresholdEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(threshold))
            {
                if (!int.TryParse(threshold, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens))
                {
                    throw new ArgumentException($"{CompactThresholdEnvironmentVariable} must be a positive token count.");
                }
                options = options with { CompactThresholdTokens = tokens };
            }

            var warmup = readEnvironment(WarmupEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(warmup))
            {
                options = options with
                {
                    EnableWarmup = warmup.Trim().ToLowerInvariant() switch
                    {
                        "true" or "1" => true,
                        "false" or "0" => false,
                        _ => throw new ArgumentException($"{WarmupEnvironmentVariable} must be true, false, 1, or 0.")
                    }
                };
            }
        }

        if (options.CompactThresholdTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(CompactThresholdTokens), "The compaction threshold must be positive.");
        }
        return options;
    }
}
