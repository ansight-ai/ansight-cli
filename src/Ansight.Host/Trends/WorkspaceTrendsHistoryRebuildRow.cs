using Microsoft.Data.Sqlite;

namespace Ansight.Host.Trends;

internal sealed record WorkspaceTrendsHistoryRebuildRow(
    WorkspaceTrendsRunContext Context,
    WorkspaceTrendsHistoryDecision Result,
    bool IsBreach);
