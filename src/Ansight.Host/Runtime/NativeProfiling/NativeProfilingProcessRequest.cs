using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record NativeProfilingProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan? Timeout = null,
    TimeSpan? InterruptAfter = null,
    string? StandardInput = null);
