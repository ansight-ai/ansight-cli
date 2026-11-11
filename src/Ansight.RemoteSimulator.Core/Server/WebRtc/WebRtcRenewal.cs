namespace Ansight.RemoteSimulator.Core.Server.WebRtc;

internal sealed record WebRtcRenewal(
    string SessionId,
    string AuthorizationLeaseId,
    DateTimeOffset AuthorizationExpiresAtUtc);
