namespace Ansight.RemoteSimulator.Core.Annotations;

public sealed record RemoteAnnotationRequest(
    string DeviceUdid,
    string Kind,
    double X,
    double Y,
    double? Width,
    double? Height,
    string Comment,
    string AgentAction,
    string? BatchId = null,
    string? FrameId = null);
