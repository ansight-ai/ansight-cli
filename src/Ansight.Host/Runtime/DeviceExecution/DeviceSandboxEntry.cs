using System.Globalization;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.DeviceExecution;

internal sealed record DeviceSandboxEntry(string Name, string RelativePath, string Kind,
    long SizeBytes, DateTimeOffset LastModifiedUtc, string Version);
