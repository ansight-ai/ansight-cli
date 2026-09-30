using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations;

internal sealed record RequestResult(
    bool IsError,
    int ErrorCode,
    string? ErrorMessage,
    JsonObject? Payload)
{
    public static RequestResult Success(JsonObject payload)
        => new(false, 0, null, payload);

    public static RequestResult Error(int errorCode, string message)
        => new(true, errorCode, message, null);

    public static RequestResult ToolResult(JsonObject payload, bool isError)
    {
        return Success(new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = ToolResultSummary.Build(payload, isError)
                }
            },
            ["structuredContent"] = payload,
            ["isError"] = isError
        });
    }
}
