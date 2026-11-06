using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record PerfettoCaptureInspection(
    string? ConfigPath,
    int? ConfiguredDurationMilliseconds,
    IReadOnlyList<string> DataSources,
    NativeProfileInspectionToolchain Toolchain,
    PerfettoQueryInspection? TraceOverview,
    IReadOnlyList<string> Warnings);
