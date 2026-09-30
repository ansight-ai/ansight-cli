namespace Ansight.Analytics;

public readonly record struct OutboxEntry(string Path, EventEnvelope Envelope);
