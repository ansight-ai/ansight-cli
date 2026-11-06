using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DeviceLocation;

internal sealed record DeviceHostTarget(
    string? DeviceIdentifier,
    string? SessionId,
    string? AppId,
    string Source)
{
    public static DeviceHostTarget Empty { get; } = new(null, null, null, string.Empty);
}
