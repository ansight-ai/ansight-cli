namespace Ansight.RemoteSimulator.Core.Annotations;

public sealed record RemoteAnnotationUpdateRequest(
    string DeviceUdid,
    string AnnotationId,
    string Kind,
    double X,
    double Y,
    double? Width,
    double? Height,
    string Comment,
    string AgentAction,
    string BatchId,
    string FrameId);
