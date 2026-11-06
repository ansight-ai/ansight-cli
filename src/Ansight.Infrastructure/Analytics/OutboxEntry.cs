namespace Ansight.Infrastructure.Analytics;

public readonly record struct OutboxEntry(string Path, EventEnvelope Envelope);
