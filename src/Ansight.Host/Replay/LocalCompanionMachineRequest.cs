
namespace Ansight.Host.Replay;

public sealed record LocalCompanionMachineRequest(
    Guid MachineId,
    string Operation,
    string? DisplayName = null);
