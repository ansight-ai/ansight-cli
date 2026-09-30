using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudTrendsUploadRequest(
    Guid TeamId,
    string AppId,
    WorkspaceTrendsHistoryResult History);
