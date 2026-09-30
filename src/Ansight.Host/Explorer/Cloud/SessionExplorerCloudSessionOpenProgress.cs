
namespace Ansight.Host.Replay;

public sealed record SessionExplorerCloudSessionOpenProgress(
    string Stage,
    string StatusText,
    double Progress);
