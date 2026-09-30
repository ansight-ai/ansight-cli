namespace Ansight.Cli.Commands.Session;

internal sealed record SessionNetworkOutput(
    string Schema,
    string SessionId,
    int TotalRequestCount,
    int MatchedRequestCount,
    IReadOnlyList<SessionNetworkRequest> Requests);
