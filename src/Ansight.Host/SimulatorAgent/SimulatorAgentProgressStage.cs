namespace Ansight.Host.SimulatorAgent;

public enum SimulatorAgentProgressStage
{
    Starting,
    Thinking,
    ModelCompleted,
    CallingTool,
    ToolCompleted,
    AppGraphUpdated,
    InstructionCompleted,
    Completed,
    TaskDiscovery
}
