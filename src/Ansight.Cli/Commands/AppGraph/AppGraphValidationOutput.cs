using Ansight.Host.Cloud.AppGraphs;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.AppGraph;

internal sealed record AppGraphValidationOutput(
    string Schema,
    Guid AppGraphId,
    Guid AppGraphVersionId,
    AppGraphValidation Validation);
