using Ansight.Host.Cloud.AppGraphs;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.AppGraph;

internal sealed record AppGraphSyncPayload(
    JsonObject Definition,
    JsonArray BindingCandidates);
