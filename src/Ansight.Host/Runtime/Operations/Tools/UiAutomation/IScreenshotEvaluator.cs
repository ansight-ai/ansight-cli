using System.Diagnostics;
using System.Text.Json.Nodes;
using Ansight.Host.SimulatorAgent.OpenAi;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal interface IScreenshotEvaluator
{
    Task<ScreenshotEvaluationResponse> EvaluateAsync(ScreenshotEvaluationRequest request, CancellationToken token);
}
