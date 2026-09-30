namespace Ansight.Host.Replay;

public sealed record LiveVisualTreeSource(string ToolId, string Label);

public sealed record LiveVisualTreeSourcesResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<LiveVisualTreeSource> Sources);
