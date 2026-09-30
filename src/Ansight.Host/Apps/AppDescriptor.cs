namespace Ansight.Host.Apps;

public sealed record AppDescriptor(
    string AppId,
    string Name,
    string? CodebasePath,
    bool AutomaticTrendsMonitoringEnabled,
    string? IconImagePath,
    bool RepositoryAutomationsEnabled,
    string? SourceKind,
    string? SourceTeamId,
    string? SourceTeamName,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    int SessionCount,
    int LiveSessionCount,
    int AnalysisCount,
    int EnrollmentInviteCount);
