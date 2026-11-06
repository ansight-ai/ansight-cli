namespace Ansight.Host.Replay;

public sealed record LiveVisualTreeSourcesResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<LiveVisualTreeSource> Sources);
