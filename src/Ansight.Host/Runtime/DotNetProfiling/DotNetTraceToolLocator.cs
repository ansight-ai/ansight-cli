using Ansight.Adb;
using Ansight.SimCtl;

namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed class DotNetTraceToolLocator
{
    private readonly IDotNetTraceProcessRunner processRunner;

    public DotNetTraceToolLocator(IDotNetTraceProcessRunner processRunner)
    {
        this.processRunner = processRunner;
    }

    public async Task<DotNetTraceToolchain> ResolveAsync(CancellationToken cancellationToken)
    {
        var dotNetTracePath = FindOnPath("dotnet-trace");
        var dotNetDsRouterPath = FindOnPath("dotnet-dsrouter");
        var adbResolution = AdbToolLocator.Resolve();
        var simCtlResolution = await SimCtlToolLocator.ResolveAsync(cancellationToken: cancellationToken);
        var dotNetTraceVersion = dotNetTracePath is null
            ? null
            : await ReadVersionAsync(dotNetTracePath, ["--version"], cancellationToken);
        var dotNetDsRouterVersion = dotNetDsRouterPath is null
            ? null
            : await ReadVersionAsync(dotNetDsRouterPath, ["--version"], cancellationToken);
        var adbPath = adbResolution.IsFound ? adbResolution.AdbPath : null;
        var adbVersion = adbPath is null
            ? null
            : await ReadVersionAsync(adbPath, ["version"], cancellationToken);
        var xcodeVersion = !simCtlResolution.IsFound
            ? null
            : await ReadVersionAsync(
                simCtlResolution.XcrunPath,
                ["xcodebuild", "-version"],
                cancellationToken);
        return new DotNetTraceToolchain(
            dotNetTracePath,
            dotNetDsRouterPath,
            adbPath,
            simCtlResolution.IsFound ? simCtlResolution.XcrunPath : null,
            simCtlResolution.IsFound ? simCtlResolution.SimCtlPath : null,
            dotNetTraceVersion,
            dotNetDsRouterVersion,
            adbVersion,
            xcodeVersion);
    }

    private async Task<string?> ReadVersionAsync(
        string toolPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new DotNetTraceProcessRequest(
                toolPath,
                arguments,
                Environment.CurrentDirectory,
                TimeSpan.FromSeconds(15)),
            outputReceived: null,
            cancellationToken);
        var output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;
        return result.IsSuccess && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
    }

    private static string? FindOnPath(string commandName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var candidateNames = OperatingSystem.IsWindows()
            ? new[] { commandName + ".exe", commandName + ".cmd", commandName }
            : new[] { commandName };
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidateName in candidateNames)
            {
                var candidatePath = Path.Combine(directory, candidateName);
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }
        }

        return null;
    }
}
