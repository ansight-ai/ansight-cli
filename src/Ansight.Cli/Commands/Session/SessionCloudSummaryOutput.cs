using Ansight.Host;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionCloudSummaryOutput(
    string Schema,
    string SourceSessionId,
    Guid? CloudSessionId,
    CloudSessionAnalysisRunResult Result);
