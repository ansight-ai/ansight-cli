namespace Ansight.Host.Profiling;

public sealed record DotNetCallTreeNode(
    string Name,
    string? Module,
    int SampleCount,
    IReadOnlyList<DotNetCallTreeNode> Children);
