using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record ScreenshotSystemUiMaskResolution(IReadOnlyList<ScreenshotIgnoreRegion> Regions, JsonArray Resolutions);
