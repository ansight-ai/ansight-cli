using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record ActionEvidenceCapture(
    string Phase,
    string? VisualTreeSnapshotId,
    string? TreeHash,
    string? ScreenshotFrameId,
    string? ScreenshotHash,
    bool Persisted,
    string Message,
    string? ScreenshotArtifactPath = null,
    string? ScreenshotFormat = null);
