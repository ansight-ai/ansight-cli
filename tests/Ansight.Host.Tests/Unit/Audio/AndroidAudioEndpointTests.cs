using Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio;

namespace Ansight.Host.Tests.Unit.Audio;

public sealed class AndroidAudioEndpointTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"ansight-audio-discovery-{Guid.NewGuid():N}");
    private readonly Dictionary<int, long> processes = [];

    public AndroidAudioEndpointTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void ResolvesOnlyTheRequestedLiveEmulatorWithItsActualAuthenticatedPort()
    {
        Write(1, "5554", 9911, "private-token");
        Write(2, "5556", 9912, "other-token");
        Write(3, "5554", 9913, "stale-token");
        processes.Remove(3);
        var endpoint = Resolver().Resolve("emulator-5554");
        Assert.Equal(9911, endpoint.Port);
        Assert.Equal(1, endpoint.ProcessId);
        Assert.Equal("127.0.0.1", endpoint.Address.Host);
        Assert.DoesNotContain("private-token", endpoint.ToString());
        Assert.DoesNotContain("Token", endpoint.GetType().GetProperties().Select(property => property.Name));
    }

    [Fact]
    public void RejectsAmbiguousEndpointsInsteadOfPickingOne()
    {
        Write(1, "5554", 9911, "first-token");
        Write(2, "5554", 9912, "second-token");
        var error = Assert.Throws<AndroidAudioEndpointException>(() => Resolver().Resolve("emulator-5554"));
        Assert.Equal("endpoint-ambiguous", error.Code);
        Assert.DoesNotContain("token", error.Message);
    }

    [Fact]
    public void RequiresAuthenticationAndDetectsProcessRestartAndTokenRotation()
    {
        Write(1, "5554", 9911, "");
        Assert.Equal("endpoint-authentication-unavailable", Assert.Throws<AndroidAudioEndpointException>(() => Resolver().Resolve("emulator-5554")).Code);
        Write(1, "5554", 9911, "first-token");
        var resolver = Resolver();
        var endpoint = resolver.Resolve("emulator-5554");
        processes[1]++;
        Assert.False(resolver.IsCurrent(endpoint));
        endpoint = resolver.Resolve("emulator-5554");
        Write(1, "5554", 9911, "changed-token");
        Assert.False(resolver.IsCurrent(endpoint));
    }

    [Theory]
    [InlineData("physical-device")]
    [InlineData("emulator-5555")]
    [InlineData("emulator-70000")]
    [InlineData("emulator-5554 extra")]
    public void RejectsNonEmulatorSerials(string serial)
        => Assert.Equal("unsupported-device", Assert.Throws<AndroidAudioEndpointException>(() => Resolver().Resolve(serial)).Code);

    [Fact]
    public void SupportsDiscoveryRecordsContainingOnlyTheAdbPort()
    {
        processes[10] = 99;
        File.WriteAllText(Path.Combine(directory, "pid_10.ini"), "port.adb=5555\ngrpc.port=9812\ngrpc.token=secret\n");
        Assert.Equal(9812, Resolver().Resolve("emulator-5554").Port);
    }

    private AndroidAudioEndpointResolver Resolver() => new([directory], process => processes.TryGetValue(process, out var stamp) ? stamp : null);

    private void Write(int process, string serial, int port, string token)
    {
        processes[process] = 100;
        File.WriteAllText(Path.Combine(directory, $"pid_{process}.ini"), $"port.serial={serial}\ngrpc.port={port}\ngrpc.token={token}\n");
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
