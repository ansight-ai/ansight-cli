namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentRunEnvironment(
    string AppId,
    string AppName,
    bool SessionWasLive,
    string AppLifecycleState,
    SimulatorAgentRunDevice? Device);
