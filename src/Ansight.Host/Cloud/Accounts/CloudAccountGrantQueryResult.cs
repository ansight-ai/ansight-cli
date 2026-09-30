using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudAccountGrantQueryResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<CloudAccountGrant> Grants)
{
    public static CloudAccountGrantQueryResult Success(IReadOnlyList<CloudAccountGrant> grants)
        => new(true, $"Found {grants.Count} cloud grant(s).", grants);

    public static CloudAccountGrantQueryResult Failure(string message)
        => new(false, message, []);
}
