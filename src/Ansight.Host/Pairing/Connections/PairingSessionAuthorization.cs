namespace Ansight.Host.Pairing.Connections;

internal sealed record PairingSessionAuthorization(
    int ProtocolVersion,
    string? GrantId,
    string MaxToolPolicy)
{
    public static PairingSessionAuthorization FromGrant(PairingClientGrant grant)
        => new(2, grant.GrantId, PairingToolPolicy.Normalize(grant.MaxToolPolicy));

    public bool AllowsPolicy(string policy)
        => PairingToolPolicy.Allows(MaxToolPolicy, policy);
}
