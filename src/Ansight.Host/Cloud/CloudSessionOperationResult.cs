using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public readonly record struct CloudSessionOperationResult(bool IsSuccess, string Message)
{
    public static CloudSessionOperationResult Success(string message) => new(true, message);

    public static CloudSessionOperationResult Failure(string message)
        => new(false, string.IsNullOrWhiteSpace(message) ? "The cloud session operation failed." : message.Trim());
}
