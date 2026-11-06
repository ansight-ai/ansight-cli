using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Runtime.Operations.Tools.Shared;

internal sealed record OperationRegistration(
    IOperation Tool,
    RepositoryTaskHostToolApplicability? TaskApplicability)
{
    public static OperationRegistration HostOnly(IOperation tool)
        => new(tool, TaskApplicability: null);

    public static OperationRegistration TaskSessionBound(IOperation tool)
        => new(tool, RepositoryTaskHostToolApplicability.SessionBound);
}
