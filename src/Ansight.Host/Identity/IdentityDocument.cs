namespace Ansight.Host.Identity;

internal sealed class IdentityDocument
{
    public required string HostName { get; set; }
    public required string PublicKeySpki { get; set; }
    public required string PrivateKeyPkcs8 { get; set; }
}
