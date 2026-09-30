namespace Ansight.RemoteSimulator.Core.Server.WebRtc;

internal sealed record OfferResponse(
    string SessionId,
    string Type,
    string Sdp,
    int FramesPerSecond,
    IReadOnlyList<string> GrantedScopes);
