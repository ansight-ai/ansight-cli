using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.NativeProfiling;

internal sealed class NativeProfileOperationServices
{
    public NativeProfileOperationServices(NativeProfilingService profiling)
    {
        Profiling = profiling ?? throw new ArgumentNullException(nameof(profiling));
    }

    public NativeProfilingService Profiling { get; }
}
