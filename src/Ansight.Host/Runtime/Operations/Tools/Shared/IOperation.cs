using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.Shared;

internal interface IOperation
{
    string Name { get; }

    JsonObject Definition { get; }

    Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId);

    Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId, OperationExecutionContext? context)
        => ExecuteAsync(arguments, correlationId);
}
