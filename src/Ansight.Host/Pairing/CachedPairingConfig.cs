namespace Ansight.Host.Models.Pairing;

public sealed class CachedPairingConfig
{
    public required PairingConfig Config { get; set; }
    public required bool Consumed { get; set; }
    public int EnrollmentUseCount { get; set; }
    public string? SourceKind { get; set; }
    public string? SourceTeamId { get; set; }
    public string? SourceTeamName { get; set; }
    public string? CompactCode { get; set; }
}
