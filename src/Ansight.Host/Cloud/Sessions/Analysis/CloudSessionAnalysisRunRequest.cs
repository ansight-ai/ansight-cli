using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public sealed record CloudSessionAnalysisRunRequest(
    Guid SessionId,
    string Kind,
    string AnalysisMode,
    string Provider,
    string Model,
    IReadOnlyList<string> SourceParts,
    long? SliceStartMs,
    long? SliceEndMs,
    string PromptInstructions);
