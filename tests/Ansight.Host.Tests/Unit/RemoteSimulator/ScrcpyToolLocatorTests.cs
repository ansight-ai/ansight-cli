using Ansight.Adb;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class ScrcpyToolLocatorTests
{
    [Fact]
    public void ResolveServerPath_FindsWindowsArchiveLayoutBesideExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ansight-scrcpy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "scrcpy.exe");
            var server = Path.Combine(directory, "scrcpy-server");
            File.WriteAllText(executable, "test executable");
            File.WriteAllText(server, "test server");
            Assert.Equal(server, ScrcpyToolLocator.ResolveServerPath(executable, configuredServer: null));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("scrcpy 3.3.1 <https://github.com/Genymobile/scrcpy>", "3.3.1")]
    [InlineData("scrcpy 4.1\nDependencies:", "4.1")]
    [InlineData("SCRCPY 4.0-dev.1", "4.0-dev.1")]
    public void ParseVersion_AcceptsOfficialVersionOutput(string output, string expected)
        => Assert.Equal(expected, ScrcpyToolLocator.ParseVersion(output));

    [Theory]
    [InlineData("")]
    [InlineData("scrcpy")]
    [InlineData("version unknown")]
    public void ParseVersion_RejectsUnrecognizedOutput(string output)
        => Assert.Null(ScrcpyToolLocator.ParseVersion(output));

    [Fact]
    public void GetCandidatePaths_PrefersConfiguredExecutable()
    {
        var configured = Path.Combine(Path.DirectorySeparatorChar.ToString(), "tools", "scrcpy");

        var firstCandidate = ScrcpyToolLocator.GetCandidatePaths(configured).First();

        Assert.Equal(configured, firstCandidate);
    }

    [Fact]
    public void EnumerateCandidates_PrefersExplicitThenBundledThenPathOnWindows()
    {
        using var fixture = new BundleFixture();
        var configured = Path.Combine(fixture.DirectoryPath, "custom.exe");
        var pathDirectory = Path.Combine(fixture.DirectoryPath, "system-tools");

        var candidates = ScrcpyToolLocator.EnumerateCandidates(
            configured, fixture.DirectoryPath, "scrcpy.exe", [pathDirectory]).Take(3).ToArray();

        Assert.Equal(["configured", "bundled", "PATH"], candidates.Select(candidate => candidate.Source));
        Assert.Equal(configured, candidates[0].Path);
        Assert.Equal(fixture.ExecutablePath, candidates[1].Path);
        Assert.Equal(Path.Combine(pathDirectory, "scrcpy.exe"), candidates[2].Path);
    }

    [Fact]
    public async Task ResolveAsync_UsesBundleWithoutPathAndIgnoresGlobalServerOverride()
    {
        using var fixture = new BundleFixture();
        var otherServer = Path.Combine(fixture.DirectoryPath, "other-server");
        File.WriteAllText(otherServer, "server from another version");
        var launcher = new VersionProcessLauncher("3.3.1");

        var result = await ScrcpyToolLocator.ResolveAsync([fixture.Candidate], launcher, otherServer);

        Assert.True(result.IsFound, result.Message);
        Assert.Equal("bundled", result.Source);
        Assert.Equal(fixture.ServerPath, result.ServerPath);
        Assert.Equal("3.3.1", result.Version);
        Assert.Single(launcher.Requests);
        Assert.Equal(["--version"], launcher.Requests[0].Arguments);
    }

    [Theory]
    [InlineData("scrcpy-server")]
    [InlineData("scrcpy.exe")]
    [InlineData("SDL2.dll")]
    public async Task ResolveAsync_RejectsModifiedBundleBeforeStartingIt(string fileName)
    {
        using var fixture = new BundleFixture();
        File.AppendAllText(Path.Combine(fixture.BundleDirectory, fileName), "modified");
        var launcher = new VersionProcessLauncher("3.3.1");

        var result = await ScrcpyToolLocator.ResolveAsync([fixture.Candidate], launcher, configuredServer: null);

        Assert.False(result.IsFound);
        Assert.Contains("checksum", result.Message);
        Assert.Contains(fileName, result.Message);
        Assert.Empty(launcher.Requests);
    }

    [Theory]
    [InlineData("bundle.json")]
    [InlineData("scrcpy-server")]
    [InlineData("SDL2.dll")]
    public async Task ResolveAsync_RejectsIncompleteBundle(string fileName)
    {
        using var fixture = new BundleFixture();
        File.Delete(Path.Combine(fixture.BundleDirectory, fileName));
        var launcher = new VersionProcessLauncher("3.3.1");

        var result = await ScrcpyToolLocator.ResolveAsync([fixture.Candidate], launcher, configuredServer: null);

        Assert.False(result.IsFound);
        Assert.Contains(fileName, result.Message);
        Assert.Empty(launcher.Requests);
    }

    [Fact]
    public async Task ResolveAsync_RejectsVersionMismatch()
    {
        using var fixture = new BundleFixture();

        var result = await ScrcpyToolLocator.ResolveAsync(
            [fixture.Candidate], new VersionProcessLauncher("4.0"), configuredServer: null);

        Assert.False(result.IsFound);
        Assert.Contains("server belongs to 3.3.1", result.Message);
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToSystemInstallWhenBundleIsDamaged()
    {
        using var fixture = new BundleFixture();
        File.WriteAllText(Path.Combine(fixture.BundleDirectory, "bundle.json"), "{}");
        var executable = Path.Combine(fixture.DirectoryPath, "scrcpy.exe");
        var server = Path.Combine(fixture.DirectoryPath, "scrcpy-server");
        File.WriteAllText(executable, "system client");
        File.WriteAllText(server, "system server");

        var result = await ScrcpyToolLocator.ResolveAsync(
            [fixture.Candidate, new ScrcpyToolLocator.ScrcpyToolCandidate(executable, "PATH")],
            new VersionProcessLauncher("4.0"),
            configuredServer: null);

        Assert.True(result.IsFound, result.Message);
        Assert.Equal("PATH", result.Source);
        Assert.Equal(server, result.ServerPath);
    }

    private sealed class BundleFixture : IDisposable
    {
        public BundleFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"ansight scrcpy {Guid.NewGuid():N}");
            Directory.CreateDirectory(BundleDirectory);
            var files = new Dictionary<string, string>();
            foreach (var fileName in new[] { "scrcpy.exe", "scrcpy-server", "SDL2.dll" })
            {
                var bytes = Encoding.UTF8.GetBytes($"test {fileName}");
                File.WriteAllBytes(Path.Combine(BundleDirectory, fileName), bytes);
                files[fileName] = Convert.ToHexString(SHA256.HashData(bytes));
            }

            File.WriteAllText(
                Path.Combine(BundleDirectory, "bundle.json"),
                JsonSerializer.Serialize(new { version = "3.3.1", files }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        public string DirectoryPath { get; }
        public string BundleDirectory => Path.Combine(DirectoryPath, "tools", "scrcpy");
        public string ExecutablePath => Path.Combine(BundleDirectory, "scrcpy.exe");
        public string ServerPath => Path.Combine(BundleDirectory, "scrcpy-server");
        public ScrcpyToolLocator.ScrcpyToolCandidate Candidate => new(ExecutablePath, "bundled");

        public void Dispose()
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed class VersionProcessLauncher : IAdbProcessLauncher
    {
        private readonly string version;

        public VersionProcessLauncher(string version)
        {
            this.version = version;
        }

        public List<AdbProcessStartRequest> Requests { get; } = [];

        public IAdbProcess Start(AdbProcessStartRequest request)
        {
            Requests.Add(request);
            return new VersionProcess(version);
        }
    }

    private sealed class VersionProcess : IAdbProcess
    {
        public VersionProcess(string version)
        {
            StandardOutput = new MemoryStream(Encoding.UTF8.GetBytes($"scrcpy {version}\n"));
        }

        public int ProcessId => 1234;
        public Stream StandardInput { get; } = new MemoryStream();
        public Stream StandardOutput { get; }
        public Stream StandardError { get; } = new MemoryStream();
        public Task<int> Completion => Task.FromResult(0);

        public void Terminate(bool force = false)
        {
        }

        public ValueTask DisposeAsync()
        {
            StandardInput.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
