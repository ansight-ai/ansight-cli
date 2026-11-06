using System.Diagnostics;
using System.Text.Json.Nodes;
using Ansight.Host.SimulatorAgent.OpenAi;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record ScreenshotEvaluationRequest(string AppId, string WorkspaceRoot, InspectionImage Image,
    InspectionImage? Reference, JsonArray Criteria, string Reasoning, Guid? TeamId);
