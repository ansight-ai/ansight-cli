using Ansight.RemoteSimulator.Core.Agent;

namespace Ansight.Cli.LocalSimulator;

internal sealed record CompanionAgentReceipt(
    string Fingerprint,
    RemoteAgentTaskLink Link);
