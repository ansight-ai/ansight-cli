using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Workspace;

internal sealed record WorkspaceAuthoringOutput(
    string Schema,
    string Operation,
    WorkspaceAuthoringResult Result,
    AppOperationResult? AppRegistration = null,
    string? RegistrationNextStep = null);
