namespace Ansight.Host.Pairing;

internal sealed class PairingCacheDocument
{
    public List<CachedPairingConfig> Items { get; set; } = [];

    public List<string> RemovedConfigIds { get; set; } = [];

    public List<PairingClientGrant> ClientGrants { get; set; } = [];
}
