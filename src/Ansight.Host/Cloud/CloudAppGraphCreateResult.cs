using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public readonly record struct CloudAppGraphCreateResult(
    bool IsSuccess,
    string Message,
    CloudAppGraphDetail? Detail)
{
    public static CloudAppGraphCreateResult Success(CloudAppGraphDetail detail, string message)
        => new(true, message, detail);

    public static CloudAppGraphCreateResult Failure(string message)
        => new(false, message, null);
}
