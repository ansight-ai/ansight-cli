using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;

internal enum RepositoryTaskSearchMatchKind
{
    Exact,
    Prefix,
    Synonym,
    Fuzzy
}
