namespace Ansight.Cli.Commands.Update;

internal sealed record CliInstallationState(
    CliInstallationReceipt? Receipt,
    string ReceiptPath)
{
    public bool IsInstallerManaged => Receipt is not null;
}
