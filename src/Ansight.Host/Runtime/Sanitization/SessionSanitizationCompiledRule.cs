using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Ansight.Host.Runtime.Sanitization;

internal sealed record SessionSanitizationCompiledRule(
    string Id,
    Regex Pattern,
    string? Replacement,
    Func<Match, bool>? ShouldRedact = null);
