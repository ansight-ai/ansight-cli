using Ansight.Adb;

namespace Ansight.Host.Runtime.NativeProfiling;

internal interface IAndroidProfileLauncher
{
    Task LaunchAsync(
        string adbPath,
        string deviceId,
        string appId,
        CancellationToken cancellationToken);
}

internal sealed class AndroidProfileLauncher : IAndroidProfileLauncher
{
    public async Task LaunchAsync(
        string adbPath,
        string deviceId,
        string appId,
        CancellationToken cancellationToken)
    {
        var adb = new AdbClient(adbPath);
        await adb.LaunchApplicationForProfilingAsync(deviceId, appId, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed class PerfettoProfileCaptureAdapter : INativeProfileCaptureAdapter
{
    private const int Android10ApiLevel = 29;
    private const int Android11ApiLevel = 30;
    internal const string TraceArtifactKind = "android-perfetto-trace";
    internal const string ConfigArtifactKind = "android-perfetto-config";
    private readonly INativeProfilingProcessRunner processRunner;
    private readonly IAndroidProfileLauncher appLauncher;
    private readonly string? adbPathOverride;

    public PerfettoProfileCaptureAdapter(
        INativeProfilingProcessRunner processRunner,
        IAndroidProfileLauncher? appLauncher = null,
        string? adbPathOverride = null)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.appLauncher = appLauncher ?? new AndroidProfileLauncher();
        this.adbPathOverride = string.IsNullOrWhiteSpace(adbPathOverride)
            ? null
            : adbPathOverride.Trim();
    }

    public string Platform => NativeProfilePlatforms.Android;

    public string Engine => "perfetto";

    public bool SupportsPreset(string preset)
        => preset is NativeProfilePresets.Launch
            or NativeProfilePresets.System
            or NativeProfilePresets.Memory
            or NativeProfilePresets.NativeHeap
            or NativeProfilePresets.ManagedHeap;

    public async Task<NativeProfileToolchain> GetToolchainAsync(CancellationToken cancellationToken)
    {
        var resolution = adbPathOverride is null
            ? AdbToolLocator.Resolve()
            : AdbToolResolution.Found(adbPathOverride, "configured");
        if (!resolution.IsFound)
        {
            return new NativeProfileToolchain(
                Platform,
                Engine,
                false,
                null,
                null,
                null,
                resolution.Message);
        }

        var versionResult = await processRunner.RunAsync(
            new NativeProfilingProcessRequest(
                resolution.AdbPath,
                ["version"],
                Environment.CurrentDirectory,
                TimeSpan.FromSeconds(15)),
            outputReceived: null,
            cancellationToken).ConfigureAwait(false);
        return new NativeProfileToolchain(
            Platform,
            Engine,
            versionResult.IsSuccess,
            resolution.AdbPath,
            ReadOutput(versionResult),
            null,
            versionResult.IsSuccess
                ? "ADB is available; device Perfetto support is checked when capture starts."
                : "ADB was found, but its version could not be read.");
    }

    public async Task<NativeProfileAdapterCaptureResult> CaptureAsync(
        NativeProfileAdapterContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var adbPath = context.CaptureToolPath;
        var apiLevel = await GetAndroidApiLevelAsync(
            adbPath,
            context.DeviceId,
            context.CapturePath,
            cancellationToken).ConfigureAwait(false);
        ValidatePresetRequirements(context.Preset, apiLevel);
        var configPath = Path.Combine(context.CapturePath, "diagnostics", "perfetto-config.pbtxt");
        var tracePath = Path.Combine(context.CapturePath, "raw", "capture.perfetto-trace");
        var remoteTracePath = $"/data/misc/perfetto-traces/ansight-{context.CaptureId}.perfetto-trace";
        var config = BuildConfig(context.AppId, context.Duration, context.Preset);
        await File.WriteAllTextAsync(
            configPath,
            config,
            cancellationToken).ConfigureAwait(false);

        try
        {
            var launchBeforeCapture = context.Preset == NativeProfilePresets.ManagedHeap;
            if (launchBeforeCapture)
            {
                context.Log($"Launching '{context.AppId}' before the ART heap snapshot.");
                await appLauncher.LaunchAsync(
                    adbPath,
                    context.DeviceId,
                    context.AppId,
                    cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
            }

            context.Log(launchBeforeCapture
                ? "Starting Perfetto ART heap capture for the running application."
                : "Starting Perfetto before the measured application launch.");
            using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var captureTask = RunAdbAsync(
                adbPath,
                [
                    "-s",
                    context.DeviceId,
                    "shell",
                    "perfetto",
                    "--txt",
                    "-c",
                    "-",
                    "-o",
                    remoteTracePath
                ],
                context.CapturePath,
                context.Duration + TimeSpan.FromSeconds(30),
                context.Log,
                captureCancellation.Token,
                config);
            try
            {
                if (!launchBeforeCapture)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
                    context.Log($"Launching '{context.AppId}' with a cold profiling launch.");
                    await appLauncher.LaunchAsync(
                        adbPath,
                        context.DeviceId,
                        context.AppId,
                        cancellationToken).ConfigureAwait(false);
                }

                EnsureSuccess("record the Perfetto trace", await captureTask.ConfigureAwait(false));
            }
            catch
            {
                captureCancellation.Cancel();
                await ObserveFailureAsync(captureTask).ConfigureAwait(false);
                throw;
            }

            context.Log("Pulling the Perfetto trace from the Android target.");
            EnsureSuccess(
                "pull the Perfetto trace",
                await RunAdbAsync(
                    adbPath,
                    ["-s", context.DeviceId, "pull", remoteTracePath, tracePath],
                    context.CapturePath,
                    TimeSpan.FromSeconds(60),
                    context.Log,
                    cancellationToken).ConfigureAwait(false));
            if (!File.Exists(tracePath) || new FileInfo(tracePath).Length == 0)
            {
                throw new InvalidOperationException(
                    "Perfetto completed without producing a usable trace file.");
            }

            return new NativeProfileAdapterCaptureResult(
                [
                    new NativeProfileProducedArtifact(
                        TraceArtifactKind,
                        tracePath,
                        IsDirectory: false,
                        Authoritative: true),
                    new NativeProfileProducedArtifact(
                        ConfigArtifactKind,
                        configPath,
                        IsDirectory: false,
                        Authoritative: false)
                ],
                BuildWarnings(context.Preset));
        }
        finally
        {
            await TryCleanRemoteArtifactsAsync(
                adbPath,
                context.DeviceId,
                remoteTracePath,
                context.CapturePath).ConfigureAwait(false);
        }
    }

    private Task<NativeProfilingProcessResult> RunAdbAsync(
        string adbPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        Action<string>? outputReceived,
        CancellationToken cancellationToken,
        string? standardInput = null)
        => processRunner.RunAsync(
            new NativeProfilingProcessRequest(
                adbPath,
                arguments,
                workingDirectory,
                timeout,
                StandardInput: standardInput),
            outputReceived,
            cancellationToken);

    private async Task TryCleanRemoteArtifactsAsync(
        string adbPath,
        string deviceId,
        string remoteTracePath,
        string workingDirectory)
    {
        try
        {
            await RunAdbAsync(
                adbPath,
                ["-s", deviceId, "shell", "rm", "-f", remoteTracePath],
                workingDirectory,
                TimeSpan.FromSeconds(15),
                outputReceived: null,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task ObserveFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task<int> GetAndroidApiLevelAsync(
        string adbPath,
        string deviceId,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunAdbAsync(
            adbPath,
            ["-s", deviceId, "shell", "getprop", "ro.build.version.sdk"],
            workingDirectory,
            TimeSpan.FromSeconds(15),
            outputReceived: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess("read the Android API level", result);
        if (!int.TryParse(result.StandardOutput.Trim(), out var apiLevel))
        {
            throw new InvalidOperationException(
                "ADB did not report a valid Android API level for the selected device.");
        }

        return apiLevel;
    }

    private static void ValidatePresetRequirements(string preset, int apiLevel)
    {
        if (apiLevel < Android10ApiLevel)
        {
            throw new InvalidOperationException(
                $"Android native profiling requires Android 10 (API {Android10ApiLevel}) or newer; "
                + $"the selected device reports API {apiLevel}.");
        }

        if (preset == NativeProfilePresets.ManagedHeap && apiLevel < Android11ApiLevel)
        {
            throw new InvalidOperationException(
                $"The managed-heap preset requires Android 11 (API {Android11ApiLevel}) or newer; "
                + $"the selected device reports API {apiLevel}.");
        }
    }

    private static string BuildConfig(string appId, TimeSpan duration, string preset)
        => preset switch
        {
            NativeProfilePresets.Launch or NativeProfilePresets.System => BuildSystemConfig(appId, duration),
            NativeProfilePresets.Memory => BuildMemoryConfig(appId, duration),
            NativeProfilePresets.NativeHeap => BuildNativeHeapConfig(appId, duration),
            NativeProfilePresets.ManagedHeap => BuildManagedHeapConfig(appId, duration),
            _ => throw new InvalidOperationException(
                $"Perfetto does not support native preset '{preset}'.")
        };

    private static string BuildSystemConfig(string appId, TimeSpan duration)
        => $$"""
             duration_ms: {{checked((int)Math.Ceiling(duration.TotalMilliseconds))}}
             buffers {
               size_kb: 65536
               fill_policy: RING_BUFFER
             }
             data_sources {
               config {
                 name: "linux.ftrace"
                 ftrace_config {
                   ftrace_events: "sched/sched_switch"
                   ftrace_events: "sched/sched_waking"
                   atrace_categories: "am"
                   atrace_categories: "wm"
                   atrace_categories: "gfx"
                   atrace_categories: "view"
                   atrace_categories: "sched"
                   atrace_categories: "freq"
                   atrace_categories: "idle"
                   atrace_apps: "{{appId}}"
                 }
               }
             }
             data_sources {
               config {
                 name: "linux.process_stats"
                 process_stats_config {
                   scan_all_processes_on_start: true
                   proc_stats_poll_ms: 1000
                 }
               }
             }
             """;

    private static string BuildMemoryConfig(string appId, TimeSpan duration)
        => $$"""
             duration_ms: {{checked((int)Math.Ceiling(duration.TotalMilliseconds))}}
             buffers {
               size_kb: 32768
               fill_policy: RING_BUFFER
             }
             buffers {
               size_kb: 16384
               fill_policy: RING_BUFFER
             }
             data_sources {
               config {
                 name: "linux.ftrace"
                 target_buffer: 0
                 ftrace_config {
                   ftrace_events: "mm_event/mm_event_record"
                   ftrace_events: "kmem/rss_stat"
                   ftrace_events: "oom/oom_score_adj_update"
                   ftrace_events: "lowmemorykiller/lowmemory_kill"
                   atrace_categories: "am"
                   atrace_categories: "wm"
                   atrace_apps: "{{appId}}"
                 }
               }
             }
             data_sources {
               config {
                 name: "linux.process_stats"
                 target_buffer: 1
                 process_stats_config {
                   scan_all_processes_on_start: true
                   proc_stats_poll_ms: 250
                 }
               }
             }
             data_sources {
               config {
                 name: "linux.sys_stats"
                 target_buffer: 1
                 sys_stats_config {
                   meminfo_period_ms: 500
                   vmstat_period_ms: 500
                 }
               }
             }
             """;

    private static string BuildNativeHeapConfig(string appId, TimeSpan duration)
        => $$"""
             duration_ms: {{checked((int)Math.Ceiling(duration.TotalMilliseconds))}}
             buffers {
               size_kb: 131072
               fill_policy: DISCARD
             }
             buffers {
               size_kb: 8192
               fill_policy: RING_BUFFER
             }
             data_sources {
               config {
                 name: "android.heapprofd"
                 target_buffer: 0
                 heapprofd_config {
                   sampling_interval_bytes: 4096
                   process_cmdline: "{{appId}}"
                   shmem_size_bytes: 8388608
                 }
               }
             }
             data_sources {
               config {
                 name: "linux.process_stats"
                 target_buffer: 1
                 process_stats_config {
                   scan_all_processes_on_start: true
                   proc_stats_poll_ms: 1000
                 }
               }
             }
             """;

    private static string BuildManagedHeapConfig(string appId, TimeSpan duration)
        => $$"""
             duration_ms: {{checked((int)Math.Ceiling(duration.TotalMilliseconds))}}
             buffers {
               size_kb: 131072
               fill_policy: DISCARD
             }
             buffers {
               size_kb: 8192
               fill_policy: RING_BUFFER
             }
             data_sources {
               config {
                 name: "android.java_hprof"
                 target_buffer: 0
                 java_hprof_config {
                   process_cmdline: "{{appId}}"
                   dump_smaps: true
                 }
               }
             }
             data_sources {
               config {
                 name: "linux.process_stats"
                 target_buffer: 1
                 process_stats_config {
                   scan_all_processes_on_start: true
                   proc_stats_poll_ms: 1000
                 }
               }
             }
             """;

    private static IReadOnlyList<string> BuildWarnings(string preset)
    {
        var warnings = new List<string>
        {
            "Perfetto Trace Processor analysis is not yet bundled; this beta capture retains the raw trace."
        };
        switch (preset)
        {
            case NativeProfilePresets.NativeHeap:
                warnings.Add(
                    "Native heap profiling requires a profileable or debuggable app on production Android builds.");
                warnings.Add(
                    "Offline native heap symbolication requires matching unstripped ELF binaries and llvm-symbolizer.");
                break;
            case NativeProfilePresets.ManagedHeap:
                warnings.Add(
                    "ART heap capture requires a profileable or debuggable app and may pause or materially perturb it.");
                warnings.Add(
                    "Use the matching R8 or ProGuard mapping.txt when deobfuscating managed heap type names.");
                break;
        }

        return warnings;
    }

    private static string? ReadOutput(NativeProfilingProcessResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;
        return result.IsSuccess && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
    }

    private static void EnsureSuccess(string operation, NativeProfilingProcessResult result)
    {
        if (result.TimedOut)
        {
            throw new TimeoutException($"ADB timed out while attempting to {operation}.");
        }

        if (result.IsSuccess)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? string.IsNullOrWhiteSpace(result.StandardOutput)
                ? $"exit code {result.ExitCode}"
                : result.StandardOutput.Trim()
            : result.StandardError.Trim();
        throw new InvalidOperationException($"ADB could not {operation}: {detail}");
    }
}
