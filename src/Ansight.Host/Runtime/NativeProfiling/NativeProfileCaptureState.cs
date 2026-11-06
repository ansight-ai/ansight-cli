namespace Ansight.Host.Runtime.NativeProfiling;

internal enum NativeProfileCaptureState
{
    Queued,
    InspectingArtifact,
    InspectingProcess,
    PreparingDevice,
    Capturing,
    DerivingArtifacts,
    Completed,
    Failed,
    Cancelled
}
