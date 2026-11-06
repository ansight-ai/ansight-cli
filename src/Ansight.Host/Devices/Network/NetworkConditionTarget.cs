using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Devices.Network;

internal sealed record NetworkConditionTarget(string DeviceId, string AppId, string? SessionId);
