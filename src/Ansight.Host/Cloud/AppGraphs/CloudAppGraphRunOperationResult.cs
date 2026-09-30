using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public readonly record struct CloudAppGraphRunOperationResult(
    bool IsSuccess,
    string Message,
    Guid RunId)
{
    public static CloudAppGraphRunOperationResult Success(Guid runId, string message)
        => new(true, message, runId);

    public static CloudAppGraphRunOperationResult Failure(string message, Guid runId = default)
        => new(false, message, runId);
}
