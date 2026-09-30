namespace Ansight.RemoteSimulator.Core.Recording;

public sealed record RemoteRecordingFrame(
    string ContentType,
    byte[] Content);
