namespace Ansight.Host.Identity;

internal sealed class RuntimeIdentity : IDisposable
{
    private readonly ECDsa identityKey;

    private RuntimeIdentity(ECDsa identityKey, string hostName)
    {
        this.identityKey = identityKey;
        HostName = hostName;
        var publicKeyBytes = identityKey.ExportSubjectPublicKeyInfo();
        PublicKeyBase64 = Convert.ToBase64String(publicKeyBytes);
        HostId = CryptoUtil.ToBase64Url(SHA256.HashData(publicKeyBytes));
        Fingerprint = HostId;
    }

    public string HostName { get; }
    public string PublicKeyBase64 { get; }
    public string HostId { get; }
    public string Fingerprint { get; }

    public static RuntimeIdentity FromPrivateKey(string hostName, byte[] privateKeyPkcs8)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
        return new RuntimeIdentity(key, hostName);
    }

    public byte[] SignUtf8(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        return identityKey.SignData(
            bytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public void Dispose()
    {
        identityKey.Dispose();
    }
}
