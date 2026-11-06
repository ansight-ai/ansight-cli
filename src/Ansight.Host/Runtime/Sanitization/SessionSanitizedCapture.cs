using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Ansight.Host.Runtime.Sanitization;

internal sealed record SessionSanitizedCapture(
    AppSessionSnapshot Snapshot,
    SessionSanitizationPolicy Policy,
    SessionSanitizationReportBuilder Report,
    IReadOnlyDictionary<string, IReadOnlyList<SessionSanitizationRegion>> ScreenshotRegionsByFrameId);
