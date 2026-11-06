using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record PerfettoQueryInspection(
    string CaptureId,
    NativeProfileInspectionToolchain Toolchain,
    string Sql,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool WasTruncated,
    int RowCount);
