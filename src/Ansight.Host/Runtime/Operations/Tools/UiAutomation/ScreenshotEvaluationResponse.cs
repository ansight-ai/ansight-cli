using System.Diagnostics;
using System.Text.Json.Nodes;
using Ansight.Host.SimulatorAgent.OpenAi;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record ScreenshotEvaluationResponse(string Text, string Model, string Reasoning, string ConfigurationRevision,
    string? ResponseId, SimulatorAgentTokenUsage Tokens);
