using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Runtime.Sanitization;

internal enum SessionScreenshotWriteAction
{
    Keep,
    Redact,
    RedactAll,
    Remove
}
