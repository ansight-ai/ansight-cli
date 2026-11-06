using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.UiAutomation;

internal interface IAppInteractionBackend
{
    string SessionId { get; }
    AppInteractionUi? Ui { get; }

    void EnsureConnected();

    Task<UiInputResult> ExecuteAsync(AppInteractionRequest request, CancellationToken cancellationToken);

    RepositoryTaskCatalog ListTasks();

    Task<RepositoryTaskRunResult> RunTaskAsync(string taskId, JsonObject? input, CancellationToken cancellationToken);

    Task<AppInteractionScreenshot?> CaptureAsync(string actionId, CancellationToken cancellationToken, bool afterInput = false);

    void Record(AppInteractionResult result);
}
