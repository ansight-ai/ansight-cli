namespace Ansight.Host.Replay;

public sealed record SessionExplorerStartRequest(
    int Port = 0,
    string? InitialSessionId = null,
    string? UrlPath = null);
