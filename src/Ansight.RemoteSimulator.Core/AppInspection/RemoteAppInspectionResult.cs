using System.Net;

namespace Ansight.RemoteSimulator.Core.AppInspection;

public sealed record RemoteAppInspectionResult(
    HttpStatusCode StatusCode,
    string ContentType,
    byte[] Content,
    string? Error = null)
{
    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;

    public static RemoteAppInspectionResult Json(byte[] content)
        => new(HttpStatusCode.OK, "application/json", content);

    public static RemoteAppInspectionResult Failure(HttpStatusCode statusCode, string error)
        => new(statusCode, "application/json", [], error);
}
