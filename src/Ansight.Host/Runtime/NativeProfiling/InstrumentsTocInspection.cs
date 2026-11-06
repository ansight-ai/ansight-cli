using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record InstrumentsTocInspection(
    string Path,
    string RootElement,
    int RunCount,
    int TableCount,
    IReadOnlyList<InstrumentsSchemaInspection> Schemas,
    IReadOnlyList<InstrumentsProcessInspection> Processes);
