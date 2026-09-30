using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host.Models.Session;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Host.Runtime.Sanitization;

namespace Ansight.Host.Explorer.TaskExtraction;

internal sealed record LocalTaskSelectorCall(
    int Sequence,
    string CallPath,
    string ToolName,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<string> DynamicFields,
    bool IsStaticallyInspectable);
