using Ansight.Infrastructure.Extensions;
using Ansight.RemoteSimulator.Core.Access;
using Ansight.RemoteSimulator.Core.Server;

namespace Ansight.Host.Tests.Unit.Notifications;

public sealed partial class HostNotificationCoordinatorTests
{
    [Fact]
    public async Task CompanionLoadedAfterStartup_NotifiesOnceAndUnsubscribesOnDispose()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var companion = new NotificationCompanionService();
        var module = $"Test.Companion{Guid.NewGuid():N}";
        OptionalExtensions.Register(module, _ => new NotificationCompanionExtension(companion));
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);
        await coordinator.InitializeAsync();

        Assert.False(runtime.Extensions.IsLoaded(module));
        runtime.Extensions.GetService<ICompanionService>(module);
        runtime.Extensions.GetService<ICompanionService>(module);
        var connection = new CompanionConnection("session-1", "phone-1", "Test phone", "iPhone",
            "iOS", "26.0", "1.0", "simulator-1", DateTimeOffset.UtcNow);
        companion.SetConnections([connection]);
        companion.SetConnections([connection]);

        Assert.Equal("Companion app connected", Assert.Single(sink.Notifications).Title);

        companion.SetConnections([]);
        companion.SetConnections([connection with { SessionId = "session-2" }]);
        Assert.Equal(2, sink.Notifications.Count);

        coordinator.Dispose();
        runtime.Extensions.GetService<ICompanionService>(module);
        companion.SetConnections([connection with { SessionId = "session-3" }]);
        Assert.Equal(2, sink.Notifications.Count);
    }

    private sealed class NotificationCompanionExtension : IOptionalExtension
    {
        private readonly ICompanionService companion;

        public NotificationCompanionExtension(ICompanionService companion)
        {
            this.companion = companion;
        }

        public object GetService(Type contract) => companion;

        public void Dispose()
        {
        }
    }

    private sealed class NotificationCompanionService : ICompanionService
    {
        private IReadOnlyList<CompanionConnection> connections = [];

        public event EventHandler? ConnectionsChanged;
        public event EventHandler? AccessStatusChanged;

        public void SetConnections(IReadOnlyList<CompanionConnection> value)
        {
            connections = value;
            ConnectionsChanged?.Invoke(this, EventArgs.Empty);
            AccessStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public IReadOnlyList<CompanionConnection> GetConnections() => connections;
        public CompanionAccessStatus GetAccessStatus() => new(CompanionAccessMode.Session, true, true, "Available", connections.Count);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public IRemoteAccessAuthorizer CreateRemoteAccessAuthorizer() => throw new NotSupportedException();
        public void ConfigureRegistration(string machineName, Guid? teamId) => throw new NotSupportedException();
        public CompanionAccessMode GetPreferredHostedAccessMode() => throw new NotSupportedException();
        public Task<CompanionAccessStatus> StartHostedAccessAsync(RemoteControlServer server, CompanionAccessMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CompanionAccessStatus> StartHostedAccessAsync(RemoteControlServer server, CompanionAccessMode mode, CompanionHostApplication hostApplication, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopHostedAccessAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CompanionAccessStatus> SetAccessModeAsync(CompanionAccessMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CompanionOperationResult> DisconnectAsync(string sessionId) => throw new NotSupportedException();
        public Task<CompanionOperationResult> DisconnectAllAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<CompanionMachine>> ListMachinesAsync(bool includeRevoked = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CompanionMachine?> GetMachineAsync(Guid machineId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CompanionOperationResult> RenameMachineAsync(Guid machineId, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CompanionOperationResult> RevokeMachineAsync(Guid machineId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CompanionOperationResult> RestoreMachineAsync(Guid machineId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CompanionOperationResult> DeleteMachineAsync(Guid machineId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void NotifyAccessStatusChanged() => throw new NotSupportedException();
        public void AttachRemoteControlServer(RemoteControlServer server) => throw new NotSupportedException();
        public void DetachRemoteControlServer(RemoteControlServer server) => throw new NotSupportedException();
        public bool TryGetRemoteControlLoopbackBaseUrl(out string baseUrl) => throw new NotSupportedException();
        public bool TryResolveSimulatorDeviceIdentifier(string reportedIdentifier, out string deviceIdentifier, out string error) => throw new NotSupportedException();
    }
}
