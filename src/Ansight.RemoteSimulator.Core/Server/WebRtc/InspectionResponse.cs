namespace Ansight.RemoteSimulator.Core.Server.WebRtc;

internal sealed record InspectionResponse(
    string Kind,
    string RequestId,
    int StatusCode,
    string ContentType,
    int ChunkIndex,
    int ChunkCount,
    string Data,
    string? Error);
