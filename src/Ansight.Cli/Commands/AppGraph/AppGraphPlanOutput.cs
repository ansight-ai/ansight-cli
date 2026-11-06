using Ansight.Host.Cloud.AppGraphs;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.AppGraph;

internal sealed record AppGraphPlanOutput(
    string Schema,
    Guid AppGraphId,
    Guid AppGraphVersionId,
    AppGraphPlan Plan);
