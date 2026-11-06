using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal readonly record struct LiveUiSwipePath(
    double StartX,
    double StartY,
    double EndX,
    double EndY,
    int DurationMilliseconds);
