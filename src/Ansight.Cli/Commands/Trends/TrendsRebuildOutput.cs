using Ansight.Host;
using Ansight.Host.Trends;
using Ansight.Infrastructure;

namespace Ansight.Cli.Commands.Trends;

internal sealed record TrendsRebuildOutput(
    string Schema,
    WorkspaceTrendsHistoryRebuildResult Rebuild);
