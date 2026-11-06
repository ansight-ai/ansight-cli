using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;

namespace Ansight.Cli.Commands.AppGraph;

internal sealed record AppGraphExplorationCandidate(
    JsonObject Definition,
    JsonObject Evidence,
    decimal Confidence,
    string Summary,
    int DestinationCount,
    int ElementActionCount);
