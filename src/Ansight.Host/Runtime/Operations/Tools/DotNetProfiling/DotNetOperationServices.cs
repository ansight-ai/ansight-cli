using Ansight.Host.Runtime.DotNetProfiling;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetOperationServices
{
    public DotNetOperationServices(
        DotNetProfilingEngine profiling)
    {
        Profiling = profiling;
        Analysis = profiling.Analysis;
    }

    public DotNetProfilingEngine Profiling { get; }

    public DotNetTraceAnalysisService Analysis { get; }

}
