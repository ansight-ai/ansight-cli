using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record AppScreenshot(
    string ArtifactPath, byte[] Bytes, DateTimeOffset CapturedAtUtc, string Format, int Width, int Height, JsonObject? Geometry = null);
