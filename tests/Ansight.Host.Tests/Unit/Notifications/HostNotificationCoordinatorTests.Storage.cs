using Ansight.Host.Tests.TestSupport;
using Ansight.Infrastructure.Security;

namespace Ansight.Host.Tests.Unit.Notifications;

public sealed partial class HostNotificationCoordinatorTests
{
    [Fact]
    public void SessionUpdates_DoNotReadEncryptedStorageEvenWhenSendingNotifications()
    {
        using var environment = new TestEnvironment();
        var storage = new CountingEncryptedStorage(new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var composition = new MefHostComposition(environment.ApplicationPaths, storage);
        using var runtime = composition.Get<RuntimeCoordinator>();
        runtime.Apps.Register(new AppRegistrationRequest("com.example.weather", "Registered Weather"));
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);
        var readsBefore = storage.ReadCount;

        for (var index = 0; index < 100; index++)
        {
            coordinator.HandleSessionUpdated(CreateSnapshot([]));
        }

        var analysis = new SessionAnalysisRecord
        {
            AnalysisId = "analysis-storage",
            AgentId = "Codex",
            AnalysisKind = SessionAnalysisKind.General,
            StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedUtc = DateTimeOffset.UtcNow,
            Success = true
        };
        var completed = CreateSnapshot([analysis]);
        coordinator.HandleSessionUpdated(completed);
        coordinator.HandleSessionUpdated(completed);

        Assert.Equal(readsBefore, storage.ReadCount);
        Assert.Equal("Registered Weather analysis completed.", Assert.Single(sink.Notifications).Body);
    }

    private sealed class CountingEncryptedStorage : IEncryptedStorage
    {
        private readonly IEncryptedStorage storage;

        public CountingEncryptedStorage(IEncryptedStorage storage)
        {
            this.storage = storage;
        }

        public int ReadCount { get; private set; }

        public string? Get(string key)
        {
            ReadCount++;

            return storage.Get(key);
        }

        public void Set(string key, string? value)
        {
            storage.Set(key, value);
        }

        public void Remove(string key)
        {
            storage.Remove(key);
        }

        public void Clear()
        {
            storage.Clear();
        }
    }
}
