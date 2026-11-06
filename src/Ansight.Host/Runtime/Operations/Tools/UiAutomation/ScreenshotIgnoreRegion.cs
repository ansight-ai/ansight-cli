using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record ScreenshotIgnoreRegion(double X, double Y, double Width, double Height);
