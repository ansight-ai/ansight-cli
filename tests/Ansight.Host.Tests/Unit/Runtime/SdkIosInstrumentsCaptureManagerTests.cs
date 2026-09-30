using Ansight.Host.Runtime.DeviceExecution;
using Ansight.Host.Tests.TestSupport;
using Ansight.Pairing.Models;
using Ansight.Infrastructure.Security;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class SdkIosInstrumentsCaptureManagerTests
{
    [Fact]
    public void IosSdkSessionWithoutProcessIdRemainsAvailableAndExplainsMissingTrace()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var environment = new TestEnvironment();
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var state = composition.Get<IRuntimeState>();
        var manager = composition.Get<SdkIosInstrumentsCaptureManager>();
        var sessionId = state.CreateSession("com.example.app", "Example", IPAddress.Loopback,
            configId: null, processSessionId: null);

        manager.Attach(sessionId, new DeviceAppProfile
        {
            Device = new DeviceProfile { OsName = "iOS" },
            App = new DeviceApplicationProfile { AppId = "com.example.app" }
        }, null);

        Assert.True(state.TryGetSessionSnapshot(sessionId, out var session));
        Assert.Equal("Connected", session!.Status);
        Assert.Equal("unavailable", session.CustomProperties?["instruments"]?["status"]?.ToString());
        Assert.Contains("process ID", session.CustomProperties?["instruments"]?["reason"]?.ToString());
        Assert.Empty(session.ArtifactSnapshots);
    }
}
