using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Runtime.Operations;

/// <summary>
/// In-process host operation catalog. This boundary has no network or protocol transport.
/// </summary>
internal interface IOperationDispatcher : IDisposable
{
    void ConfigureOptionalExtensions(Ansight.Infrastructure.Extensions.OptionalExtensions extensions) { }

    JsonObject BuildToolsListResult();

    IReadOnlyList<AppSessionSnapshot> GetSessionSummaries();

    bool TryGetSessionSnapshot(string sessionId, out AppSessionSnapshot? snapshot);

    bool IsSessionConnected(string sessionId);

    Task<JsonObject?> GetSessionAppToolCatalogAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<RequestResult> CallToolAsync(
        string toolName,
        JsonObject? arguments,
        string? correlationId = null,
        OperationExecutionContext? context = null);

    IDisposable BeginSimulatorAgentRun(string sessionId, string? targetDeviceIdentifier = null);

    IAppInteractionContext CreateAppInteractionContext(string sessionId, string? repositoryRootPath = null);

    void ConfigureDevicePermissions(IDeviceService devices, RuntimeOptions options) { }

    void ConfigureAudioInjection(Ansight.Host.Audio.AudioInjectionEngine engine);

    void ConfigureUiInputDriver(IUiInputDriver? driver);

    void ConfigureUiAccessibilityDriver(IUiAccessibilityDriver? driver);

    void ConfigureDeviceLocationPlayback(DeviceLocationPlaybackService playback, IDeviceService devices);

    void ConfigureDeviceLocationDriver(IDeviceLocationDriver? driver);

    void ConfigureDeviceLifecycleDriver(IDeviceLifecycleDriver? driver);

    void ConfigureRepositoryTaskRuntime(string executablePath);

    RepositoryTaskCatalog InspectRepositoryTasks(string repositoryRootPath, string appId);

    Task<RepositoryTaskRunResult> RunRepositoryTaskAsync(
        string repositoryRootPath,
        string appId,
        string sessionId,
        string taskId,
        JsonObject? suppliedInput,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? secretValues = null);
}
