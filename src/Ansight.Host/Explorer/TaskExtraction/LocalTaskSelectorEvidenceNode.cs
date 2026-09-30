using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host.Models.Session;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Host.Runtime.Sanitization;

namespace Ansight.Host.Explorer.TaskExtraction;

internal sealed record LocalTaskSelectorEvidenceNode(
    string? NodeId,
    string? AutomationId,
    string? Role,
    string? Type,
    IReadOnlySet<string> TextValues,
    IReadOnlySet<string> Actions,
    IReadOnlySet<string> AncestorAutomationIds,
    string SnapshotId,
    DateTimeOffset CapturedAtUtc,
    string VisualTreeKind,
    string Source);
