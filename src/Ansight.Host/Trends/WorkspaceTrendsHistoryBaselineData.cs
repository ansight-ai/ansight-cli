using Microsoft.Data.Sqlite;

namespace Ansight.Host.Trends;

internal sealed record WorkspaceTrendsHistoryBaselineData(
    IReadOnlyList<double> Values,
    WorkspaceTrendsHistoryStatus? PreviousStatus,
    int ConsecutiveBreachCount,
    string? BaselineAppVersion);
