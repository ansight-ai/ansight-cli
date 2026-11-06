using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Devices.Network;

internal sealed record NetworkConditionStep(NetworkConditionProfile Profile, int DurationMs);
