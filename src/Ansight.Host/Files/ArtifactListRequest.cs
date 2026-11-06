namespace Ansight.Host.Files;

public sealed record ArtifactListRequest(
    string SessionId, string? Search = null, string? Provider = null, string? Format = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, long? MinBytes = null, long? MaxBytes = null,
    int Offset = 0, int Limit = 200, string Sort = "captured-at");
