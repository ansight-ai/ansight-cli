namespace Ansight.Cli.HostConnection;

internal static class ControlProtocol
{
    public const string RequestSchema = "ansight.cli-control-request/v1";
    public const string HostLeaseRequestSchema = "ansight.cli-control-request/v2";
    public const string ResponseSchema = "ansight.cli-control-response/v1";
    public const string HostLeaseResponseSchema = "ansight.cli-control-response/v2";
    public const string OutputSchema = "ansight.cli-control-output/v1";
    public const string CancelSchema = "ansight.cli-control-cancel/v1";

    public static bool SupportsCooperativeCancellation(CliArguments arguments)
        => arguments.Positionals.Count >= 2
           && arguments.Positionals[0].Equals("audio", StringComparison.OrdinalIgnoreCase)
           && arguments.Positionals[1].Equals("inject", StringComparison.OrdinalIgnoreCase);
}
