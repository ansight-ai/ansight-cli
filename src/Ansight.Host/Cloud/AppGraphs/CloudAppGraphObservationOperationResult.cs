using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public readonly record struct CloudAppGraphObservationOperationResult(
    bool IsSuccess,
    string Message,
    Guid ObservationId)
{
    public static CloudAppGraphObservationOperationResult Success(Guid observationId, string message)
        => new(true, message, observationId);

    public static CloudAppGraphObservationOperationResult Failure(string message, Guid observationId = default)
        => new(false, message, observationId);
}
