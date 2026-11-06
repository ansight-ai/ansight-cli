using System.Globalization;
using System.Runtime.InteropServices;

namespace Ansight.Host.Runtime.DeviceExecution;

internal sealed record DeviceProcessSample(int ProcessId, string Identity, long? CpuNanoseconds,
    long? ResidentBytes, long? FootprintBytes = null, long? ProportionalBytes = null);
