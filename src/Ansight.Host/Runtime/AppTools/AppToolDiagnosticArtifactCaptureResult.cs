namespace Ansight.Host.Runtime.AppTools;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

internal sealed record AppToolDiagnosticArtifactCaptureResult(
    bool IsSupported,
    bool IsCaptured,
    string? SnapshotId,
    string? Message)
{
    public static AppToolDiagnosticArtifactCaptureResult NotSupported { get; } = new(
        IsSupported: false,
        IsCaptured: false,
        SnapshotId: null,
        Message: null);
}
