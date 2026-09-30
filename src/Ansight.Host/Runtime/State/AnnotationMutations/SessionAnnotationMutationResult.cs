using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.State;

internal sealed record SessionAnnotationMutationResult(bool IsSuccess, string Message, JsonObject? Payload)
{
    public static SessionAnnotationMutationResult Failure(string message) => new(false, message, null);
    public static SessionAnnotationMutationResult Success(JsonObject payload) => new(true, "Annotation mutation completed.", payload);
}
