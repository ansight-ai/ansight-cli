using System.Text.Json.Nodes;

namespace Ansight.Host.Devices.Network;

public sealed record NetworkConditioningResult(bool IsSuccess, JsonObject Payload);
