namespace Ansight.Host.Models.Session;

/// <summary>Passive measurements for the final HTTP transaction, after redirects/retries.</summary>
public sealed class SessionNetworkPerformance
{
    public double? TimeToFirstByteMilliseconds { get; init; }
    public double? DnsMilliseconds { get; init; }
    /// <summary>Connection establishment including TLS, when used.</summary>
    public double? ConnectMilliseconds { get; init; }
    public double? TlsMilliseconds { get; init; }
    public double? UploadDurationMilliseconds { get; init; }
    public double? DownloadDurationMilliseconds { get; init; }
    /// <summary>Transferred body bytes, independent of retained content and Content-Length.</summary>
    public long? UploadedBytes { get; init; }
    public long? DownloadedBytes { get; init; }
    public bool? ReusedConnection { get; init; }
}
