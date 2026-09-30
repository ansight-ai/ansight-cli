using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ansight.Infrastructure.Preferences;
using Ansight.RemoteSimulator.Core.Access;
using Ansight.RemoteSimulator.Core.Server;
using LocalMachineIdentity = Ansight.Host.Identity.RuntimeIdentity;

namespace Ansight.Host.Companion;
public interface ICompanionService : IAsyncDisposable
{
    event EventHandler? ConnectionsChanged;
    event EventHandler? AccessStatusChanged;
    IRemoteAccessAuthorizer CreateRemoteAccessAuthorizer();
    void ConfigureRegistration(string machineName, Guid? teamId);
    CompanionAccessMode GetPreferredHostedAccessMode();
    Task<CompanionAccessStatus> StartHostedAccessAsync(RemoteControlServer server, CompanionAccessMode mode, CancellationToken cancellationToken = default);
    Task<CompanionAccessStatus> StartHostedAccessAsync(RemoteControlServer server, CompanionAccessMode mode, CompanionHostApplication hostApplication, CancellationToken cancellationToken = default);
    Task StopHostedAccessAsync(CancellationToken cancellationToken = default);
    CompanionAccessStatus GetAccessStatus();
    Task<CompanionAccessStatus> SetAccessModeAsync(CompanionAccessMode mode, CancellationToken cancellationToken = default);
    IReadOnlyList<CompanionConnection> GetConnections();
    Task<CompanionOperationResult> DisconnectAsync(string sessionId);
    Task<CompanionOperationResult> DisconnectAllAsync();
    Task<IReadOnlyList<CompanionMachine>> ListMachinesAsync(bool includeRevoked = false, CancellationToken cancellationToken = default);
    Task<CompanionMachine?> GetMachineAsync(Guid machineId, CancellationToken cancellationToken = default);
    Task<CompanionOperationResult> RenameMachineAsync(Guid machineId, string displayName, CancellationToken cancellationToken = default);
    Task<CompanionOperationResult> RevokeMachineAsync(Guid machineId, CancellationToken cancellationToken = default);
    Task<CompanionOperationResult> RestoreMachineAsync(Guid machineId, CancellationToken cancellationToken = default);
    Task<CompanionOperationResult> DeleteMachineAsync(Guid machineId, CancellationToken cancellationToken = default);
    void NotifyAccessStatusChanged();
    void AttachRemoteControlServer(RemoteControlServer server);
    void DetachRemoteControlServer(RemoteControlServer server);
    bool TryGetRemoteControlLoopbackBaseUrl(out string baseUrl);
    bool TryResolveSimulatorDeviceIdentifier(string reportedIdentifier, out string deviceIdentifier, out string error);
}
