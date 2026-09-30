namespace Ansight.Host.Cloud;

public sealed record CloudSessionLookupRequest(
    Guid? SharedSessionId,
    string? SourceSessionId,
    Guid? TeamId,
    bool IncludeArchived)
{
    public string? Search { get; init; }

    public int Limit { get; init; } = 200;
}
