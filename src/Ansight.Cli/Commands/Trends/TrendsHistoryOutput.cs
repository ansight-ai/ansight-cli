using Ansight.Host;
using Ansight.Host.Trends;
using Ansight.Infrastructure;

namespace Ansight.Cli.Commands.Trends;

internal sealed record TrendsHistoryOutput(
    string Schema,
    WorkspaceTrendsHistoryResult History);
