namespace Ansight.Host.SimulatorAgent.Runs;

internal static class RunRequestContext
{
    private const string correlationIdPrefix = "simulator-agent-";
    private const string semanticOnlyCorrelationIdPrefix = "simulator-agent-semantic-only-";

    public static string CreateCorrelationId(bool allowScreenshotOcr = true)
        => $"{(allowScreenshotOcr ? correlationIdPrefix : semanticOnlyCorrelationIdPrefix)}{Guid.NewGuid():N}";

    public static bool IsTestRunCorrelationId(string? correlationId)
        => !string.IsNullOrWhiteSpace(correlationId)
           && correlationId.StartsWith(correlationIdPrefix, StringComparison.Ordinal);

    public static bool AllowsScreenshotOcr(string? correlationId)
        => string.IsNullOrWhiteSpace(correlationId)
           || !correlationId.StartsWith(semanticOnlyCorrelationIdPrefix, StringComparison.Ordinal);
}
