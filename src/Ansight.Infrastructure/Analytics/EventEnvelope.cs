namespace Ansight.Analytics;

public sealed record EventEnvelope(
    string InsertId,
    string EventName,
    string DistinctId,
    EventLevel Level,
    IReadOnlyDictionary<string, object?> Properties,
    DateTimeOffset TimestampUtc);
