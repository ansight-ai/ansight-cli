using System.Diagnostics;
using System.Runtime.InteropServices;
using Ansight.Adb;
using Ansight.Infrastructure.Preferences;
using Ansight.SimCtl;

namespace Ansight.Host.Workspaces.Execution;

internal sealed class NativeTestVideoRecorder : ITestVideoRecorder
{
    private readonly IUserPreferences preferences;

    public NativeTestVideoRecorder(IUserPreferences preferences)
    {
        this.preferences = preferences;
    }

    public async Task<ITestVideoRecording> StartAsync(TestVideoRecordingRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            throw new NotSupportedException("Test video recording v1 requires a macOS or Linux host.");
        }

        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        string source;

        if (request.Platform == DevicePlatforms.Ios && request.IsVirtual && OperatingSystem.IsMacOS())
        {
            var tool = await SimCtlToolLocator.ResolveAsync(preferences.XcodePath, cancellationToken).ConfigureAwait(false);

            if (!tool.IsFound)
            {
                throw new IOException(tool.Message);
            }

            source = "simctl";
            start.FileName = tool.XcrunPath;
            start.Environment["DEVELOPER_DIR"] = tool.DeveloperDirectory;
            AddArguments(start, ["simctl", "io", request.DeviceIdentifier, "recordVideo", "--codec=h264", request.OutputPath]);
        }
        else if (request.Platform == DevicePlatforms.Android)
        {
            var tool = await ScrcpyToolLocator.ResolveAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!tool.IsFound)
            {
                throw new IOException(tool.Message);
            }

            var adb = AdbToolLocator.Resolve(preferences.AdbPath);

            if (!adb.IsFound)
            {
                throw new IOException(adb.Message);
            }

            source = "scrcpy";
            start.FileName = tool.ExecutablePath;
            start.Environment["ADB"] = adb.AdbPath;
            start.Environment["SCRCPY_SERVER_PATH"] = tool.ServerPath;
            AddArguments(start, ["--serial", request.DeviceIdentifier, "--no-audio", "--no-playback",
                "--no-window", "--no-control", "--video-codec=h264", "--max-fps=24", "--max-size=1280",
                "--video-bit-rate=1200000", "--record-format=mp4", "--record", request.OutputPath]);
        }
        else
        {
            throw new NotSupportedException("Test video recording v1 supports iOS Simulator and Android devices/emulators.");
        }

        return await NativeTestVideoRecording.StartAsync(start, source, cancellationToken).ConfigureAwait(false);
    }

    private static void AddArguments(ProcessStartInfo start, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
    }
}
