namespace Ansight.Host.Models.Session;

/// <summary>A continuous display recording on the session's UTC timeline.</summary>
public sealed record SessionVideoRecording(
    string RecordingId,
    string RunId,
    DateTimeOffset StartedUtc,
    long DurationUs,
    int Width,
    int Height,
    long ByteSize,
    string Source,
    string Status = "completed")
{
    public string TimingSource { get; init; } = "host-observed";
    public string Codec { get; init; } = "h264";
    public string EntryPath => $"session-video/{RecordingId}.mp4";
    public DateTimeOffset EndedUtc => StartedUtc.AddTicks(checked(DurationUs * 10));

    internal void Validate()
    {
        if (string.IsNullOrEmpty(RecordingId) || RecordingId.Length > 128
            || RecordingId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
            || DurationUs <= 0 || DurationUs > (DateTimeOffset.MaxValue - StartedUtc).Ticks / 10
            || Width <= 0 || Height <= 0 || ByteSize <= 0)
            throw new InvalidDataException("Invalid session video recording metadata.");
        _ = EndedUtc;
    }
}
