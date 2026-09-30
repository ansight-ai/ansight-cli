namespace Ansight.Host.Models.Session;

public sealed class SessionReplaySource
{
    public const string CloudKind = "cloud";

    public string Kind { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? Detail { get; init; }

    public string? SourceId { get; init; }
}
