using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

internal sealed class RepositoryTaskSourceCapture
{
    private readonly List<RepositoryTaskSourceModule> modules = [];
    private string? captureError;

    public bool TryRead(JsonObject message)
    {
        if (message["type"]?.GetValue<string>() != "source") return false;
        if (message["module"] is JsonObject module)
        {
            var snapshot = module.Deserialize<RepositoryTaskSourceModule>(JsonUtil.Compact);
            if (snapshot is not null) modules.Add(snapshot);
        }
        if (message["error"] is JsonValue error) captureError = error.GetValue<string>();
        return true;
    }

    public RepositoryTaskSourceTrace Finish(string taskId) => new(taskId, modules.ToArray(), captureError);
}
