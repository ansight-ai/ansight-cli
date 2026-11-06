using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Ansight.Host.Runtime.Sanitization;

internal readonly record struct SessionSanitizationRegion(
    double X,
    double Y,
    double Width,
    double Height);
