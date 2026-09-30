namespace Ansight.Host.Replay;

public sealed record SessionTimelineExtractionResult(
    bool IsSuccess,
    string Message,
    string? ExtractedSessionId);
