namespace Ansight.Host.SimulatorAgent;

/// <summary>Local model execution uses the developer's credential without contacting Ansight.</summary>
internal static class LocalModelAccess
{
    internal const string ConfigurationMessage = "Set OPENAI_API_KEY to run AI-assisted tools locally. "
        + "For optional Ansight-hosted AI, set ANSIGHT_AI_TRANSPORT=cloud and run 'ansight account login'.";

    internal static bool IsCloudSelected => string.Equals(
        Environment.GetEnvironmentVariable("ANSIGHT_AI_TRANSPORT")?.Trim(), "cloud", StringComparison.OrdinalIgnoreCase);

    internal static string? ResolveApiKey()
    {
        var value = Environment.GetEnvironmentVariable("OPENAI_API_KEY")?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
