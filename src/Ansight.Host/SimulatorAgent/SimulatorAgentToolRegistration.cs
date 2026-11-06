using System.Globalization;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentToolRegistration(string Name, string SchemaSha256, string Location);
