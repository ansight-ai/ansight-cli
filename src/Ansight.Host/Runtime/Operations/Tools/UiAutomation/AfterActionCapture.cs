using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record AfterActionCapture(
    ActionEvidenceCapture Evidence,
    LiveUiTreeCapture? Capture)
{
    public bool ScrollSettled { get; init; } = true;
}
