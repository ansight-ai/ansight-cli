using Ansight.Host;

namespace Ansight.Cli.HostConnection;

internal sealed record InteractionBatchResult(
    string Schema,
    string Id,
    string Command,
    string SessionId,
    bool Succeeded,
    string? Error,
    string Message,
    IReadOnlyList<AppInteractionResult> Results,
    IReadOnlyList<string> SkippedIds,
    AppInteractionScreenshot? Screenshot,
    AppInteractionTiming Timing,
    AppInteractionUi? Ui);
