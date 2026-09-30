namespace Ansight.Cli.HostConnection;

internal sealed record ControlOutput(
    string Schema,
    string Stream,
    string Value);
