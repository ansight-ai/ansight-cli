using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ansight.Host.SimulatorAgent.OpenAi;

internal sealed record OpenAiRequest(
    string ApiKey,
    string Model,
    string Instructions,
    JsonArray Input,
    JsonArray Tools,
    string ReasoningEffort,
    string PromptCacheKey,
    int MaximumOutputTokens)
{
    public JsonArray? IncrementalInput { get; init; }

    public bool StartNewConversation { get; init; }

    public int? CompactThresholdTokens { get; init; }

    public bool UsePromptCacheBreakpoint { get; init; }

    public string? ReplayReason { get; init; }

    public bool CompletionOnly { get; init; }

    public IModelExecutionTransport? Transport { get; init; }

    internal async ValueTask<string> ResolveApiKeyAsync(CancellationToken cancellationToken)
        => Transport is null ? ApiKey.Trim() : await Transport.ResolveAccessKeyAsync(cancellationToken).ConfigureAwait(false);
}
