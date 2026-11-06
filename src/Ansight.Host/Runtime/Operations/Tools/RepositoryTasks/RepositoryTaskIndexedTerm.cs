using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;

internal sealed record RepositoryTaskIndexedTerm(
    string Term,
    double Weight,
    string Source);
