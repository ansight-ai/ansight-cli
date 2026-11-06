using Microsoft.Data.Sqlite;

namespace Ansight.Host.Trends;

internal sealed record WorkspaceTrendsHistoryRebuildScope(
    string AppId,
    string? AppVersion,
    bool HasAppVersionFilter,
    string? DecisionId);
