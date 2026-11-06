namespace Ansight.Host.Sanitization;

internal sealed class SessionSanitizationRegexRule
{
    public required string Id { get; init; }
    public required string Pattern { get; init; }
    public string? Replacement { get; init; }
    public bool IgnoreCase { get; init; } = true;
}
