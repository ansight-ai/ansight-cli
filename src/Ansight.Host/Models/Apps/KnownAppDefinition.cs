namespace Ansight.Host.Models.Apps;

public sealed class KnownAppDefinition
{
    public required string AppId { get; set; }
    public required string Name { get; set; }
    public required string IconGlyph { get; set; }
    public string? IconImagePath { get; set; }
    public string? CodebasePath { get; set; }
    public bool RepositoryAutomationsEnabled { get; set; }
    public string? SourceKind { get; set; }
    public string? SourceTeamId { get; set; }
    public string? SourceTeamName { get; set; }
    public DateTimeOffset? SourceSyncedAtUtc { get; set; }
    public required DateTimeOffset FirstSeenUtc { get; set; }
    public required DateTimeOffset LastSeenUtc { get; set; }
}
