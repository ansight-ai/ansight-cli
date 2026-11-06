using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Replay;

internal sealed record ReplaySessionTaggingOutput(
    bool IsSuccess,
    string Message,
    IReadOnlyList<string> Tags);
