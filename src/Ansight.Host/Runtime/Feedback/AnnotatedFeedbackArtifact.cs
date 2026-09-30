using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Feedback;

internal sealed record AnnotatedFeedbackArtifact(
    string EvidenceId,
    string Name,
    string Kind,
    string MimeType,
    string FileName,
    string Status,
    string? Reason,
    long? SizeBytes,
    byte[]? Bytes);
