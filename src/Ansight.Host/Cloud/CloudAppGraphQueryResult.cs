using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public readonly record struct CloudAppGraphQueryResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<CloudAppGraphSummary> Graphs)
{
    public static CloudAppGraphQueryResult Success(IReadOnlyList<CloudAppGraphSummary> graphs)
        => new(true, string.Empty, graphs);

    public static CloudAppGraphQueryResult Failure(string message)
        => new(false, message, Array.Empty<CloudAppGraphSummary>());
}
