using System.IO.Compression;
using System.Text;
using SdkPairingDiscoveryHint = Ansight.Pairing.Models.PairingDiscoveryHint;

namespace Ansight.Host.Pairing;

public static class PairingConfigDocumentFactory
{
    private const string InviteDocumentSchemaName = "ansight.enrollment-invite-document.v2";
    private const string EnrollmentCodePrefix = "ans2";

    public static string Serialize(PairingConfigDocument document, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!PairingProtocolPolicy.IsEnabledSchema(document.Config.Schema))
        {
            throw new NotSupportedException("Only the current enrollment-invite schema is supported.");
        }

        var model = new PairingConfigDocumentJsonModel
        {
            Schema = InviteDocumentSchemaName,
            Invite = PublicPairingConfigJson.CreateJsonModel(document.Config),
            Discovery = document.Discovery
        };
        return JsonSerializer.Serialize(model, indented ? JsonUtil.Pretty : JsonUtil.Compact);
    }

    public static string SerializeCompactCode(PairingConfigDocument document)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(Serialize(document));
        using var compressedStream = new MemoryStream();
        using (var gzip = new GZipStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(jsonBytes, 0, jsonBytes.Length);
        }

        return $"{EnrollmentCodePrefix}:{CryptoUtil.ToBase64Url(compressedStream.ToArray())}";
    }

    public static PairingConfigDocument Create(
        global::Ansight.Host.Models.Pairing.PairingConfig pairingConfig,
        IReadOnlyList<string> hostAddresses,
        string? hostName,
        string? wifiName,
        string source,
        DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(pairingConfig);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return new PairingConfigDocument
        {
            Schema = InviteDocumentSchemaName,
            Config = pairingConfig,
            Discovery = new SdkPairingDiscoveryHint
            {
                Schema = SdkPairingDiscoveryHint.SchemaName,
                Source = source.Trim(),
                HostAddresses = hostAddresses?.Where(address => !string.IsNullOrWhiteSpace(address))
                    .Select(address => address.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                DiscoveryPort = pairingConfig.Host.DiscoveryPort,
                HostName = string.IsNullOrWhiteSpace(hostName) ? null : hostName.Trim(),
                WifiName = string.IsNullOrWhiteSpace(wifiName) ? null : wifiName.Trim(),
                CapturedAt = capturedAtUtc ?? DateTimeOffset.UtcNow
            }
        };
    }

    private sealed class PairingConfigDocumentJsonModel
    {
        public required string Schema { get; init; }

        public required object Invite { get; init; }

        public SdkPairingDiscoveryHint? Discovery { get; init; }
    }

    public sealed class PairingConfigDocument
    {
        public required string Schema { get; init; }

        public required global::Ansight.Host.Models.Pairing.PairingConfig Config { get; init; }

        public SdkPairingDiscoveryHint? Discovery { get; init; }
    }
}
