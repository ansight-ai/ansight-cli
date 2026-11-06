using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record NativeProfilingProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool WasInterrupted = false)
{
    public bool IsSuccess => ExitCode == 0 && !TimedOut;
}
