using System.Text;
using System.Globalization;
using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.BinaryTransfers;

internal sealed record BinaryToolArtifactTransferRegistration(
    string SessionId,
    string TransferId,
    string ArtifactPath,
    string ToolId,
    string Name,
    string Kind,
    string MimeType,
    string FileName,
    string? ProviderId,
    string? ArtifactId,
    DateTimeOffset CapturedAtUtc,
    bool CaptureSessionArtifactSnapshot,
    Task Completion);
