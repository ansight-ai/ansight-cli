namespace Ansight.Host.Runtime.DotNetProfiling;

internal enum DotNetTraceCaptureState
{
    Queued,
    InspectingArtifact,
    PreparingDevice,
    Capturing,
    DerivingArtifacts,
    Completed,
    Failed,
    Cancelled
}
