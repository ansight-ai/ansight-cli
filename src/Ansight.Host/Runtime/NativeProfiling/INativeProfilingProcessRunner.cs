using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ansight.Host.Runtime.NativeProfiling;

internal interface INativeProfilingProcessRunner
{
    Task<NativeProfilingProcessResult> RunAsync(
        NativeProfilingProcessRequest request,
        Action<string>? outputReceived,
        CancellationToken cancellationToken);
}
