using System.Text.Json.Serialization;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentRunCost(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    long ProviderCostMicros,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    long? CustomerCostMicros,
    string Currency,
    string Status)
{
    public long? CostMicros
    {
        get => CustomerCostMicros;
        init => CustomerCostMicros = value;
    }
}
