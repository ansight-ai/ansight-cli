using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudRegisteredAppQueryResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<CloudRegisteredApp> Apps)
{
    public static CloudRegisteredAppQueryResult Success(IReadOnlyList<CloudRegisteredApp> apps)
        => new(true, $"Found {apps.Count} registered cloud app(s).", apps);

    public static CloudRegisteredAppQueryResult Failure(string message)
        => new(false, message, []);
}
