using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.DeviceExecution;

internal sealed record PhysicalIosActivitySample(
    DateTimeOffset CapturedAtUtc,
    long? CpuMillicores,
    long? PhysicalFootprintBytes,
    long? RealMemoryBytes);
