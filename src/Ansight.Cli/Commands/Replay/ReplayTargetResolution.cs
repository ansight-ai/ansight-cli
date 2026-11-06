using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Replay;

internal sealed record ReplayTargetResolution(
    AppSessionSnapshot Session,
    WorkspaceTestTarget? LaunchedTarget);
