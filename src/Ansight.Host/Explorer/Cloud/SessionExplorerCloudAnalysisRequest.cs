
namespace Ansight.Host.Replay;

public sealed record SessionExplorerCloudAnalysisRequest(
    string? Kind,
    string? AnalysisMode,
    string? Provider,
    string? Model,
    IReadOnlyList<string>? SourceParts,
    long? SliceStartMs,
    long? SliceEndMs,
    string? PromptInstructions);
