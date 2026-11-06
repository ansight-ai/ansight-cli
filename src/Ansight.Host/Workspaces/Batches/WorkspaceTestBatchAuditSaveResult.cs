using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Host.Workspaces.Batches;

internal sealed record WorkspaceTestBatchAuditSaveResult(string? FilePath, string? ErrorMessage);
