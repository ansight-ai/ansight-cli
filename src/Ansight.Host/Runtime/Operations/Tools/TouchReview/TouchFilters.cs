namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal readonly record struct TouchFilters(
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    HashSet<string> Actions,
    HashSet<long> PointerIds,
    int? MinPointerCount,
    int? MaxPointerCount)
{
    public static TouchFilters Empty { get; } = new(
        null,
        null,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<long>(),
        null,
        null);
}
