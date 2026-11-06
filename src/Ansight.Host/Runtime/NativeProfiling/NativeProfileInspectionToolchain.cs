using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record NativeProfileInspectionToolchain(
    bool IsAvailable,
    string? ToolPath,
    string? Version,
    string Message);
