namespace Ansight.RemoteSimulator.Core.Server.WebRtc;

internal sealed record OfferRequest(
    string DeviceUdid,
    int? FramesPerSecond,
    string Type,
    string Sdp,
    IReadOnlyList<string>? GrantedScopes);
