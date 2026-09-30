namespace Ansight.Host.Pairing;

using System.IO.Compression;
using System.Text;

public static class PairingConfigCompactJsonExport
{
    private const string EnrollmentCodePrefix = "ans2";

    public static string Serialize(PairingConfig pairingConfig)
    {
        ArgumentNullException.ThrowIfNull(pairingConfig);
        return PublicPairingConfigJson.Serialize(pairingConfig, indented: true);
    }

    public static bool TrySerializeCompactCode(
        string? compactCode,
        string expectedConfigId,
        out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(compactCode)
            || string.IsNullOrWhiteSpace(expectedConfigId))
        {
            return false;
        }

        var separator = compactCode.IndexOf(':');
        if (separator <= 0
            || !string.Equals(compactCode[..separator], EnrollmentCodePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var compressed = CryptoUtil.FromBase64Url(compactCode[(separator + 1)..]);
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            var payload = Encoding.UTF8.GetString(output.ToArray());
            var invite = PublicPairingConfigJson.TryDeserialize(payload);
            if (invite is null
                || !string.Equals(invite.ConfigId, expectedConfigId, StringComparison.Ordinal))
            {
                return false;
            }

            json = Serialize(invite);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
