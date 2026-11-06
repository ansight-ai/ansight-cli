using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.DeviceExecution;

namespace Ansight.Host.Devices.Network;

internal interface INetworkConditioningBackend : IAsyncDisposable
{
    string Name { get; }
    DateTimeOffset ExpiresAtUtc { get; }

    Task ApplyAsync(NetworkConditionProfile profile, IReadOnlyList<NetworkConditionStep> steps, CancellationToken cancellationToken);

    JsonObject Status();
}
