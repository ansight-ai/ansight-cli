using System.Text.Json.Nodes;
namespace Ansight.Host.Runtime.Operations;
internal interface IOperationExtension
{
    Task<RequestResult> ExecuteAsync(OperationServices services, string toolName, JsonObject? arguments,
        string? correlationId, OperationExecutionContext? context);
}
