namespace Ansight.RemoteSimulator.Core.Companion;

public sealed record RemoteCompanionConnection(
    string SessionId,
    RemoteCompanionDevice Device,
    string TargetDeviceUdid,
    DateTimeOffset ConnectedAtUtc);
