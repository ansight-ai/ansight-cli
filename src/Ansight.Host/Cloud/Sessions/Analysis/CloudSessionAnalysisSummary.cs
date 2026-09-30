using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public sealed class CloudSessionAnalysisSummary
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("session_id")]
    public Guid SessionId { get; init; }

    [JsonPropertyName("team_id")]
    public Guid TeamId { get; init; }

    [JsonPropertyName("requested_by_user_id")]
    public string? RequestedByUserId { get; init; }

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "openai";

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "analysis";

    [JsonPropertyName("analysis_mode")]
    public string AnalysisMode { get; init; } = "fast";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "queued";

    [JsonPropertyName("source_parts")]
    public string[] SourceParts { get; init; } = [];

    [JsonPropertyName("slice_start_ms")]
    public long? SliceStartMs { get; init; }

    [JsonPropertyName("slice_end_ms")]
    public long? SliceEndMs { get; init; }

    [JsonPropertyName("max_duration_seconds")]
    public int MaxDurationSeconds { get; init; }

    [JsonPropertyName("prompt_instructions")]
    public string PromptInstructions { get; init; } = string.Empty;

    [JsonPropertyName("archived_at")]
    public DateTimeOffset? ArchivedAt { get; init; }

    [JsonPropertyName("archived_by_user_id")]
    public string? ArchivedByUserId { get; init; }

    [JsonPropertyName("progress_stage")]
    public string ProgressStage { get; init; } = "queued";

    [JsonPropertyName("progress_message")]
    public string ProgressMessage { get; init; } = string.Empty;

    [JsonPropertyName("progress_percent")]
    public int ProgressPercent { get; init; }

    [JsonPropertyName("progress_updated_at")]
    public DateTimeOffset? ProgressUpdatedAt { get; init; }

    [JsonPropertyName("requested_at")]
    public DateTimeOffset RequestedAt { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("summary_markdown")]
    public string? SummaryMarkdown { get; init; }

    [JsonPropertyName("mermaid_definition")]
    public string? MermaidDefinition { get; init; }

    [JsonPropertyName("result_json")]
    public JsonNode? ResultJson { get; init; }

    [JsonPropertyName("warnings")]
    public string[] Warnings { get; init; } = [];

    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; init; }

    [JsonPropertyName("consumed_tokens")]
    public long ConsumedTokens { get; init; }

    [JsonPropertyName("cached_input_tokens")]
    public long CachedInputTokens { get; init; }

    [JsonPropertyName("cache_write_tokens")]
    public long CacheWriteTokens { get; init; }

    [JsonPropertyName("reasoning_tokens")]
    public long ReasoningTokens { get; init; }

    [JsonPropertyName("tool_tokens")]
    public long ToolTokens { get; init; }

    [JsonPropertyName("estimated_cost_micros")]
    public long? EstimatedCostMicros { get; init; }

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "USD";
}
