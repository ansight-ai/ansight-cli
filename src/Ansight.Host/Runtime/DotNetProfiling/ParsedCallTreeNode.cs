namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record ParsedCallTreeNode(
    string Name,
    string? Module,
    int SampleCount,
    IReadOnlyList<ParsedCallTreeNode> Children);
