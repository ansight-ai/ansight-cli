namespace Ansight.Host.Runtime;

public sealed record RemoteRunnerRegistration(Guid MachineId, Guid TeamId, string DisplayName, IReadOnlyList<string> Kinds);
