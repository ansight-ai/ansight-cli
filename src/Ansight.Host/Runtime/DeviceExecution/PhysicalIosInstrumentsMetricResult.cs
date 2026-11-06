using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.DeviceExecution;

internal sealed record PhysicalIosInstrumentsMetricResult(
    int RowCount,
    int CpuSampleCount,
    int MemorySampleCount,
    string TableSchema);
