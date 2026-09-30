namespace Ansight.Host.Models.Session;

public sealed class SessionNetworkRequest
{
    public const string SchemaName = "ansight.network-request.v1";

    public string Schema { get; init; } = SchemaName;
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required double DurationMilliseconds { get; init; }
    public required string Method { get; init; }
    public required string Url { get; init; }
    public bool RedactSensitiveData { get; init; } = true;
    public string? Protocol { get; init; }
    public IReadOnlyList<SessionNetworkHeader> RequestHeaders { get; init; } = Array.Empty<SessionNetworkHeader>();
    public long? RequestBodySizeBytes { get; init; }
    public SessionNetworkBody? RequestBody { get; init; }
    public int? StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public IReadOnlyList<SessionNetworkHeader> ResponseHeaders { get; init; } = Array.Empty<SessionNetworkHeader>();
    public long? ResponseBodySizeBytes { get; init; }
    public SessionNetworkBody? ResponseBody { get; init; }
    public string? ErrorType { get; init; }
    public string? ErrorMessage { get; init; }
}
