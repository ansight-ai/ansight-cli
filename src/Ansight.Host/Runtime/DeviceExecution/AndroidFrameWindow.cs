using System.Globalization;

namespace Ansight.Host.Runtime.DeviceExecution;

internal sealed record AndroidFrameWindow(bool Supported, long LastCompletedNanoseconds, int Frames,
    long? FramesPerSecond, bool Overflow);
