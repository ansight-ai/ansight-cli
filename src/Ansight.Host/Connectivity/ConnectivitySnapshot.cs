using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Host.Connectivity;

public sealed record ConnectivitySnapshot
{
    public string Availability { get; init; } = "unknown";
    public IReadOnlyList<string> Transports { get; init; } = [];
    public bool? Vpn { get; init; }
    public bool? InternetValidated { get; init; }
    public bool? CaptivePortal { get; init; }
    public bool? Metered { get; init; }
    public bool? Expensive { get; init; }
    public bool? LowDataMode { get; init; }
}
