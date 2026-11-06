using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal readonly record struct LiveUiPinchPath(
    double PrimaryStartX,
    double PrimaryStartY,
    double SecondaryStartX,
    double SecondaryStartY,
    double PrimaryEndX,
    double PrimaryEndY,
    double SecondaryEndX,
    double SecondaryEndY,
    int DurationMilliseconds);
