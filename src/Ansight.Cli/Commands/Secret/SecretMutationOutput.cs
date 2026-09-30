using Ansight.Host;

namespace Ansight.Cli.Commands.Secret;

internal sealed record SecretMutationOutput(
    string Schema,
    string Operation,
    bool IsSuccess,
    SimulatorAgentSecretMetadata? Secret);
