namespace Ansight.Host.Models.Session;

public sealed record SessionTranscriptSegment(
    string SegmentId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Text);
