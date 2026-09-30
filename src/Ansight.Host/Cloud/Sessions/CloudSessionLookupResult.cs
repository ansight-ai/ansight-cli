namespace Ansight.Host.Cloud;

public readonly record struct CloudSessionLookupResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<CloudSessionSummary> Sessions)
{
    public static CloudSessionLookupResult Success(IReadOnlyList<CloudSessionSummary> sessions)
        => new(true, string.Empty, sessions);

    public static CloudSessionLookupResult Failure(string message)
        => new(false, string.IsNullOrWhiteSpace(message) ? "Unable to load shared sessions." : message.Trim(), Array.Empty<CloudSessionSummary>());
}
