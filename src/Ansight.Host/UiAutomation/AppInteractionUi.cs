using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.UiAutomation;

public sealed record AppInteractionUi(
    string Status, string? Source, string? SnapshotId, DateTimeOffset? CapturedAtUtc,
    IReadOnlyList<AppInteractionUiNode> Nodes, bool Truncated, string? Message = null);
