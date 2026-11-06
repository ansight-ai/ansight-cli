using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record NativeProfileCaptureInspection(
    string AnalyzerVersion,
    string CaptureId,
    string Platform,
    string Engine,
    string Preset,
    string State,
    int RequestedDurationSeconds,
    double? CaptureElapsedSeconds,
    string CaptureDirectoryPath,
    NativeProfileArtifactLocation? AuthoritativeArtifact,
    IReadOnlyList<NativeProfileArtifactLocation> Artifacts,
    InstrumentsTocInspection? Instruments,
    PerfettoCaptureInspection? Perfetto,
    IReadOnlyList<string> Warnings);
