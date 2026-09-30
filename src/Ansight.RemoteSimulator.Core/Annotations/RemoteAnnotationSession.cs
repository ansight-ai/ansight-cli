namespace Ansight.RemoteSimulator.Core.Annotations;

public sealed record RemoteAnnotationSession(
    string DeviceUdid,
    string SessionId,
    string AppName,
    string AppId);
