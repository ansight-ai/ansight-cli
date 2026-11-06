using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public readonly record struct CloudAppGraphDetailResult(
    bool IsSuccess,
    string Message,
    CloudAppGraphDetail? Detail)
{
    public static CloudAppGraphDetailResult Success(CloudAppGraphDetail detail)
        => new(true, string.Empty, detail);

    public static CloudAppGraphDetailResult Failure(string message)
        => new(false, message, null);
}
