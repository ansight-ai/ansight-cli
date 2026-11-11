using System.Text.Json.Nodes;

namespace Ansight.Host.Workspaces.Catalog;

public sealed record WorkspaceTestPreTask(string TaskId, JsonObject? Input = null);
