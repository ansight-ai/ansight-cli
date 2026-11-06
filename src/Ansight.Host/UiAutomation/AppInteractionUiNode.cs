using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.UiAutomation;

public sealed record AppInteractionUiNode(
    string? AutomationId, string? Text, string Role, string? Value, bool Enabled,
    IReadOnlyList<string> Actions, IReadOnlyList<string> AncestorAutomationIds);
