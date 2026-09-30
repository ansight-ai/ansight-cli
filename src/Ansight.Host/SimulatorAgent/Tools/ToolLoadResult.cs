using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.Tools;

/// <summary>Contains a tool-load response, additional callable tools, and model guidance.</summary>
internal sealed record ToolLoadResult(
    ToolCallResult Result,
    JsonArray AdditionalTools,
    string Guidance);
