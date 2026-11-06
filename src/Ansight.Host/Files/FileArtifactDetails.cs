namespace Ansight.Host.Files;

public sealed record FileArtifactDetails(
    string DownloadedAtUtc,
    string Sha256,
    string Sha1,
    string Md5);
