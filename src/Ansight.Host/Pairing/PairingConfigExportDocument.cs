namespace Ansight.Host.Pairing;

internal sealed class PairingConfigExportDocument
{
    public const string SchemaName = "ansight.enrollment-invite-export.v2";

    public string Schema { get; set; } = SchemaName;

    public DateTimeOffset ExportedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public required PairingConfig Config { get; set; }

    public bool Consumed { get; set; }
}
