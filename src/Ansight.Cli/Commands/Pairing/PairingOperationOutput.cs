using Ansight.Host;

namespace Ansight.Cli.Commands.Pairing;

internal sealed record PairingOperationOutput(
    string Schema,
    string Operation,
    PairingOperationResult Result);
