using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.UiAutomation;

public sealed record AppInteractionTiming(double InputMs, double CaptureMs, double TotalMs);
