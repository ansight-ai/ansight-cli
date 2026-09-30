namespace Ansight.RemoteSimulator.Core.Annotations;

public sealed record RemoteAnnotationDeleteRequest(
    string DeviceUdid,
    string AnnotationId,
    string BatchId,
    string FrameId);
