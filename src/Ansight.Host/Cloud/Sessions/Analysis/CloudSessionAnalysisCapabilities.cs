using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public sealed class CloudSessionAnalysisCapabilities
{
    [JsonPropertyName("session_id")]
    public Guid SessionId { get; init; }

    [JsonPropertyName("team_id")]
    public Guid TeamId { get; init; }

    [JsonPropertyName("analysis_enabled")]
    public bool AnalysisEnabled { get; init; }

    [JsonPropertyName("mermaid_enabled")]
    public bool MermaidEnabled { get; init; }

    [JsonPropertyName("max_session_duration_seconds")]
    public int MaxSessionDurationSeconds { get; init; }

    [JsonPropertyName("thorough_analysis_enabled")]
    public bool ThoroughAnalysisEnabled { get; init; }

    [JsonPropertyName("max_ai_screenshot_count")]
    public int MaxAiScreenshotCount { get; init; }

    [JsonPropertyName("max_ai_screenshot_request_rounds")]
    public int MaxAiScreenshotRequestRounds { get; init; }

    [JsonPropertyName("max_ai_extraction_wall_clock_seconds")]
    public int MaxAiExtractionWallClockSeconds { get; init; }

    [JsonPropertyName("openai_configured")]
    public bool OpenAiConfigured { get; init; }

    [JsonPropertyName("anthropic_configured")]
    public bool AnthropicConfigured { get; init; }

    [JsonPropertyName("gemini_configured")]
    public bool GeminiConfigured { get; init; }

    [JsonPropertyName("allocated_tokens")]
    public long AllocatedTokens { get; init; }

    [JsonPropertyName("consumed_tokens")]
    public long ConsumedTokens { get; init; }

    [JsonPropertyName("remaining_tokens")]
    public long RemainingTokens { get; init; }
}
