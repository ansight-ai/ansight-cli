namespace Ansight.Host.Runtime.AppTools;

using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

internal sealed record AppToolBridgeRequestContext(
    string Source,
    string? Operation = null,
    string? CorrelationId = null);
