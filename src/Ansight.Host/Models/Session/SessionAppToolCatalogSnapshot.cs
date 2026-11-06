namespace Ansight.Host.Models.Session;

using System.Text.Json.Nodes;
using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Pairing.Models;

public sealed record SessionAppToolCatalogSnapshot(
    string Schema,
    DateTimeOffset CapturedAtUtc,
    JsonObject ToolCatalog,
    JsonObject? ArtifactCatalog)
{
    public string Status { get; init; } = "ready";
    public string? Message { get; init; }
}
