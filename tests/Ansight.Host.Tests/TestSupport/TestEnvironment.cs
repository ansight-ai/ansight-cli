namespace Ansight.Host.Tests.TestSupport;

internal sealed class TestEnvironment : IDisposable
{
    private bool disposed;
    private readonly IDisposable protocolOverride;

    public TestEnvironment(int webSocketSessionPortCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(webSocketSessionPortCount, 1);

        var discoveryPort = FindFreeUdpPort();
        var webSocketPort = FindFreeTcpPort();
        var sessionPortRange = FindFreeTcpPortRange(webSocketSessionPortCount);

        RootPath = Path.Combine(
            Path.GetTempPath(),
            "ansight-host-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        ApplicationPaths = new DataToolApplicationPaths(RootPath);
        SecureStorageFilePath = Path.Combine(RootPath, "secure-storage.json");
        SecureStorageKeyFilePath = FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(
            SecureStorageFilePath);
        FileEncryptionKeyProvider.CreateKeyFile(SecureStorageKeyFilePath);
        protocolOverride = ProtocolDefaults.PushOverride(
            discoveryPort: discoveryPort,
            webSocketPort: webSocketPort,
            webSocketPath: "/ws",
            webSocketSessionPortRangeStart: sessionPortRange.Start,
            webSocketSessionPortRangeEnd: sessionPortRange.End);
    }

    public string RootPath { get; }

    public string SecureStorageFilePath { get; }

    public string SecureStorageKeyFilePath { get; }

    public DataToolApplicationPaths ApplicationPaths { get; }

    public RuntimeCoordinator CreateRuntime(bool enableRepositoryAutomations = false)
    {
        return new RuntimeCoordinator(new RuntimeOptions
        {
            BaseFolderPath = RootPath,
            SecureStorageFilePath = SecureStorageFilePath,
            SecureStorageKeyFilePath = SecureStorageKeyFilePath,
            EnableRepositoryAutomations = enableRepositoryAutomations
        });
    }

    public SeededPairingConfig SeedPairingConfig(string appId, string appName)
    {
        var composition = new MefHostComposition(
            ApplicationPaths,
            new FileBackedEncryptedStorage(SecureStorageFilePath));
        var hostIdentity = composition.Get<IIdentityStore>().Current;
        var pairingCache = composition.Get<IPairingConfigCache>();
        var configId = Guid.NewGuid().ToString("N");
        var token = CryptoUtil.CreateBase64UrlRandom(32);
        var now = DateTimeOffset.UtcNow;

        pairingCache.Add(
            new PairingConfig
            {
                Schema = PairingConfig.SchemaName,
                ConfigId = configId,
                AppId = appId,
                AppName = appName,
                IssuedAt = now,
                ExpiresAt = now.AddMinutes(30),
                MinProtocolVersion = 2,
                AllowedTransports = [PairingTransportNames.Ws],
                Host = new PairingHost
                {
                    HostId = hostIdentity.HostId,
                    HostName = hostIdentity.HostName,
                    DiscoveryPort = ProtocolDefaults.DiscoveryPort
                },
                Enrollment = new PairingEnrollment
                {
                    Secret = token,
                    ExpiresAt = now.AddMinutes(30),
                    GrantExpiresAt = now.AddDays(30),
                    MaxUses = 1,
                    MaxToolPolicy = "read"
                }
            });

        return new SeededPairingConfig(configId, token, appId, appName);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        protocolOverride.Dispose();

        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures in tests.
        }
    }

    private static int FindFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static PortRange FindFreeTcpPortRange(int count)
    {
        const int firstCandidate = 49152;
        var lastCandidate = IPEndPoint.MaxPort - count + 1;
        var random = new Random();

        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var start = random.Next(firstCandidate, lastCandidate + 1);
            var listeners = new List<TcpListener>(count);
            try
            {
                for (var offset = 0; offset < count; offset++)
                {
                    var listener = new TcpListener(IPAddress.Loopback, start + offset);
                    listener.Start();
                    listeners.Add(listener);
                }

                return new PortRange(start, start + count - 1);
            }
            catch (SocketException)
            {
                // Try another contiguous range.
            }
            finally
            {
                foreach (var listener in listeners)
                {
                    listener.Stop();
                }
            }
        }

        throw new InvalidOperationException(
            $"Unable to find {count} contiguous TCP ports for the test environment.");
    }

    private static int FindFreeUdpPort()
    {
        using var client = new UdpClient(0);
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }

    private readonly record struct PortRange(int Start, int End);
}
