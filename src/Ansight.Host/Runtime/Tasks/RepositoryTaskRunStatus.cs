using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Runtime.Tasks;

public enum RepositoryTaskRunStatus
{
    Passed,
    Failed,
    Error,
    Inconclusive,
    TimedOut,
    Cancelled,
    Rejected
}
