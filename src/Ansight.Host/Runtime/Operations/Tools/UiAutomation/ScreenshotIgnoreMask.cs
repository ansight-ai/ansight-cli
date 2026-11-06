using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record ScreenshotIgnoreMask(bool[] Pixels, long IgnoredPixelCount);
