using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DeviceLifecycle;

internal sealed record DeviceLifecycleTarget(
    string DeviceIdentifier,
    string? BundleIdentifier,
    string? SessionId,
    string Source)
{
    public static DeviceLifecycleTarget Empty { get; } = new(string.Empty, null, null, string.Empty);
}
