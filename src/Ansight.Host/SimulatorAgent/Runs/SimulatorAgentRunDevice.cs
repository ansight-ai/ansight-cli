namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentRunDevice(
    string? Identifier,
    string? Manufacturer,
    string? Model,
    string? FormFactor,
    string? OperatingSystemName,
    string? OperatingSystemVersion,
    bool? IsVirtual,
    bool? IsEmulator);
