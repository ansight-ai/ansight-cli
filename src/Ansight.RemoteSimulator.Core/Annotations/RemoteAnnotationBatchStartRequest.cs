namespace Ansight.RemoteSimulator.Core.Annotations;

public sealed record RemoteAnnotationBatchStartRequest(
    string DeviceUdid,
    string BatchId);
