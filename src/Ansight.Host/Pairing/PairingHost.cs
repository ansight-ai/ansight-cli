namespace Ansight.Host.Models.Pairing;

public sealed class PairingHost
{
    public string? HostId { get; set; }
    public string? HostName { get; set; }
    public int DiscoveryPort { get; set; } = ProtocolDefaults.DiscoveryPort;
}
