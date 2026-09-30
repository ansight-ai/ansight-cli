using Ansight.Host;

namespace Ansight.Cli.Commands.Secret;

internal sealed record SecretListOutput(
    string Schema,
    string AppId,
    IReadOnlyList<SimulatorAgentSecretMetadata> Secrets);
