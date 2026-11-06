using System.Text.Json.Serialization;

namespace Ansight.Host.Workspaces.History;

public static class WorkspaceTestHistoryStatuses
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Pending = "pending";
    public const string Skipped = "skipped";
}
