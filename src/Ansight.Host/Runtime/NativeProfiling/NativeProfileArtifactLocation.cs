using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record NativeProfileArtifactLocation(
    string CaptureId,
    string Platform,
    string Path,
    NativeProfileArtifact Artifact);
