namespace Ansight.Host.SimulatorAgent;

/// <summary>Durably records received provider usage before execution continues.</summary>
public interface IModelUsageRecorder
{
    void RecordUsage(SimulatorAgentModelPassUsage usage);
}
