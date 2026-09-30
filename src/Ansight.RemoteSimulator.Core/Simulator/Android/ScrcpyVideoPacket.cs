namespace Ansight.RemoteSimulator.Core.Simulator.Android;

public sealed record ScrcpyVideoPacket(
    byte[] AccessUnit,
    long PresentationTimestampMicroseconds,
    bool IsKeyFrame,
    int VideoWidth = 0,
    int VideoHeight = 0);
