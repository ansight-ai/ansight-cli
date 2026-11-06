using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.UiAutomation;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppInteractionTarget(
    string? AutomationId = null, string? Text = null, string? Role = null, string? AncestorAutomationId = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AutomationId) && string.IsNullOrWhiteSpace(Text))
        {
            throw new ArgumentException("target requires automationId or exact text; role and ancestorAutomationId narrow the match.");
        }

        foreach (var value in new[] { AutomationId, Text, Role, AncestorAutomationId })
        {
            if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 1024))
            {
                throw new ArgumentException("Target fields must be non-empty strings of at most 1024 characters.");
            }
        }
    }
}
