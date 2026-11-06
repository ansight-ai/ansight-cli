using System.Globalization;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.DeviceExecution;

internal sealed record DeviceSandboxChunk(DeviceSandboxEntry File, byte[] Bytes, bool HasMore);
