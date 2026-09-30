namespace Ansight.Host.Runtime.NativeLogs;

using System.Globalization;
using System.Threading.Channels;
using Ansight.Adb;
using Ansight.Pairing.Models;
using Ansight.SimCtl;
using Ansight.Infrastructure.Preferences;

[Export(typeof(INativeSessionLogCaptureManager))]
internal sealed class NativeSessionLogCaptureManager : INativeSessionLogCaptureManager
{
    private static readonly Ansight.Infrastructure.Logging.ILogger log = Ansight.Infrastructure.Logging.Logger.Create();
    private static readonly TimeSpan androidProcessRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan batchWindow = TimeSpan.FromMilliseconds(200);
    private const int maximumBatchSize = 128;
    private const int mappedEntryBufferCapacity = 4_096;

    private readonly IRuntimeState runtimeState;
    private readonly IUserPreferences userPreferences;
    private readonly Lock captureGate = new();
    private readonly Dictionary<string, NativeCaptureHandle> capturesBySessionId = new(StringComparer.Ordinal);
    private readonly NativeLogStringPool stringPool = new(4_096);

    [ImportingConstructor]
    public NativeSessionLogCaptureManager(IRuntimeState runtimeState, IUserPreferences userPreferences)
    {
        this.runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        this.userPreferences = userPreferences ?? throw new ArgumentNullException(nameof(userPreferences));
    }

    public async Task AttachAsync(string sessionId, DeviceAppProfile? profile, string? profileJson)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || profile is null)
        {
            return;
        }

        await StopAsync(sessionId, "Native log target changed.").ConfigureAwait(false);
        if (!userPreferences.CaptureNativeSessionLogs)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var handle = new NativeCaptureHandle(cancellation);
        lock (captureGate)
        {
            capturesBySessionId[sessionId] = handle;
        }

        handle.Task = Task.Run(() => AttachCoreAsync(sessionId, profile, profileJson, cancellation.Token));
    }

    public async Task StopAsync(string sessionId, string reason)
    {
        NativeCaptureHandle? handle;
        lock (captureGate)
        {
            if (!capturesBySessionId.Remove(sessionId, out handle))
            {
                return;
            }
        }

        handle.Cancellation.Cancel();
        try
        {
            await handle.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected completion path.
        }
        finally
        {
            handle.Cancellation.Dispose();
        }

        if (runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot) && snapshot is not null)
        {
            foreach (var stream in snapshot.LogStreams.Where(stream =>
                         stream.Status == SessionLogStreamStatuses.Active
                         && IsNativeStream(stream.StreamId)))
            {
                runtimeState.SetSessionLogStreamStatus(
                    sessionId,
                    stream.StreamId,
                    SessionLogStreamStatuses.Completed,
                    reason,
                    DateTimeOffset.UtcNow);
            }
        }
    }

    private async Task AttachCoreAsync(
        string sessionId,
        DeviceAppProfile profile,
        string? profileJson,
        CancellationToken cancellationToken)
    {
        var osName = profile.Device?.OsName?.Trim().ToLowerInvariant() ?? string.Empty;
        try
        {
            if (string.Equals(osName, "android", StringComparison.Ordinal))
            {
                await CaptureAndroidAsync(sessionId, profile, profileJson, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(osName, "ios", StringComparison.Ordinal))
            {
                await CaptureAppleSimulatorAsync(sessionId, profile, profileJson, cancellationToken).ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                MarkActiveNativeStreamCompleted(sessionId, osName, "Native log process completed.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var streamId = string.Equals(osName, "android", StringComparison.Ordinal)
                ? SessionLogStreamIds.AndroidLogcat
                : SessionLogStreamIds.AppleUnifiedLog;
            runtimeState.SetSessionLogStreamStatus(
                sessionId,
                streamId,
                SessionLogStreamStatuses.Failed,
                ex.Message,
                DateTimeOffset.UtcNow);
            log.Warning($"native_log_capture_failed sessionId={sessionId} streamId={streamId} reason=\"{ex.Message}\"");
        }
    }

    private async Task CaptureAndroidAsync(
        string sessionId,
        DeviceAppProfile profile,
        string? profileJson,
        CancellationToken cancellationToken)
    {
        var packageIdentifier = profile.App?.AppId?.Trim();
        var reportedProcessId = profile.App?.ProcessId;
        var sessionStartedUtc = runtimeState.TryGetSessionSnapshot(sessionId, out var sessionSnapshot)
            ? sessionSnapshot?.CreatedUtc ?? DateTimeOffset.UtcNow
            : DateTimeOffset.UtcNow;
        runtimeState.EnsureSessionLogStream(
            sessionId,
            CreatePendingStream(
                SessionLogStreamIds.AndroidLogcat,
                SessionLogStreamKinds.AndroidLogcat,
                "Android Logcat",
                packageIdentifier,
                reportedProcessId));
        if (string.IsNullOrWhiteSpace(packageIdentifier))
        {
            MarkUnavailable(sessionId, SessionLogStreamIds.AndroidLogcat, "The session profile did not include an Android package identifier.");
            return;
        }

        var tool = AdbToolLocator.Resolve(userPreferences.AdbPath);
        if (!tool.IsFound)
        {
            MarkUnavailable(sessionId, SessionLogStreamIds.AndroidLogcat, tool.Message);
            return;
        }

        var client = new AdbClient(tool.AdbPath);
        var exactSerial = string.IsNullOrWhiteSpace(profileJson) ? null
            : System.Text.Json.Nodes.JsonNode.Parse(profileJson)?["device"]?["nativeDeviceId"]?.GetValue<string>();
        var devices = (await client.GetDevicesAsync(cancellationToken).ConfigureAwait(false))
            .Where(device => device.IsConnected && (exactSerial is null || device.Serial == exactSerial))
            .ToArray();
        var matches = new List<AndroidTarget>();
        foreach (var device in devices)
        {
            var processIds = await client.GetProcessIdsAsync(device.Serial, packageIdentifier, cancellationToken).ConfigureAwait(false);
            if (processIds.Count == 0
                || (reportedProcessId.HasValue && !processIds.Contains(reportedProcessId.Value)))
            {
                continue;
            }

            matches.Add(new AndroidTarget(device, processIds));
        }

        if (matches.Count != 1)
        {
            MarkUnavailable(
                sessionId,
                SessionLogStreamIds.AndroidLogcat,
                matches.Count == 0
                    ? "No connected ADB device matched the session package and process."
                    : "More than one ADB device matched the session; native logs were not attached.");
            return;
        }

        var target = matches[0];
        runtimeState.EnsureSessionLogStream(
            sessionId,
            new SessionLogStream
            {
                StreamId = SessionLogStreamIds.AndroidLogcat,
                Kind = SessionLogStreamKinds.AndroidLogcat,
                DisplayName = "Android Logcat",
                Status = SessionLogStreamStatuses.Active,
                StartedUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["deviceSerial"] = target.Device.Serial,
                    ["deviceName"] = target.Device.Model ?? target.Device.Device ?? target.Device.Serial,
                    ["packageIdentifier"] = packageIdentifier,
                    ["adbPath"] = tool.AdbPath
                }
            });

        var entryChannel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(mappedEntryBufferCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        var writerTask = WriteBatchesAsync(
            sessionId,
            SessionLogStreamIds.AndroidLogcat,
            entryChannel.Reader,
            cancellationToken);
        var captures = new Dictionary<int, AndroidProcessCapture>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IReadOnlyList<int> processIds;
                try
                {
                    processIds = await client.GetProcessIdsAsync(
                        target.Device.Serial,
                        packageIdentifier,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    log.Warning($"adb_process_refresh_failed sessionId={sessionId} reason=\"{ex.Message}\"");
                    await Task.Delay(androidProcessRefreshInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var stoppedProcessId in captures
                             .Where(pair => !processIds.Contains(pair.Key) || pair.Value.PumpTask.IsCompleted)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    await captures[stoppedProcessId].DisposeAsync().ConfigureAwait(false);
                    captures.Remove(stoppedProcessId);
                }

                foreach (var processId in processIds.Where(processId => !captures.ContainsKey(processId)))
                {
                    var stream = await client.StartLogcatAsync(
                        new AdbLogcatRequest(target.Device.Serial, processId),
                        cancellationToken).ConfigureAwait(false);
                    captures[processId] = new AndroidProcessCapture(
                        stream,
                        PumpAdbEntriesAsync(stream, entryChannel.Writer, sessionStartedUtc, cancellationToken));
                }

                await Task.Delay(androidProcessRefreshInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var capture in captures.Values)
            {
                await capture.DisposeAsync().ConfigureAwait(false);
            }

            entryChannel.Writer.TryComplete();
            await AwaitCaptureTaskAsync(writerTask).ConfigureAwait(false);
        }
    }

    private async Task CaptureAppleSimulatorAsync(
        string sessionId,
        DeviceAppProfile profile,
        string? profileJson,
        CancellationToken cancellationToken)
    {
        var processId = profile.App?.ProcessId;
        var nativeDeviceId = ResolveNativeDeviceId(profileJson);
        runtimeState.EnsureSessionLogStream(
            sessionId,
            CreatePendingStream(
                SessionLogStreamIds.AppleUnifiedLog,
                SessionLogStreamKinds.AppleUnifiedLog,
                "Apple Unified Log",
                profile.App?.AppId,
                processId));
        if (profile.Device?.IsVirtual != true && profile.Device?.IsEmulator != true)
        {
            MarkUnavailable(sessionId, SessionLogStreamIds.AppleUnifiedLog, "SimCtl log capture is only available for simulator sessions.");
            return;
        }

        if (!processId.HasValue || processId.Value <= 0)
        {
            MarkUnavailable(sessionId, SessionLogStreamIds.AppleUnifiedLog, "The session profile did not include an application process ID.");
            return;
        }

        var tool = await SimCtlToolLocator.ResolveAsync(userPreferences.XcodePath, cancellationToken).ConfigureAwait(false);
        if (!tool.IsFound)
        {
            MarkUnavailable(sessionId, SessionLogStreamIds.AppleUnifiedLog, tool.Message);
            return;
        }

        var client = new SimCtlClient(tool);
        var bootedDevices = (await client.GetDevicesAsync(cancellationToken).ConfigureAwait(false))
            .Where(device => device.IsBooted)
            .ToArray();
        SimCtlDevice? target = null;
        if (!string.IsNullOrWhiteSpace(nativeDeviceId))
        {
            target = bootedDevices.FirstOrDefault(device => string.Equals(device.Udid, nativeDeviceId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var matches = new List<SimCtlDevice>();
            foreach (var device in bootedDevices)
            {
                if (await client.ProcessExistsAsync(device.Udid, processId.Value, cancellationToken).ConfigureAwait(false))
                {
                    matches.Add(device);
                }
            }

            if (matches.Count == 1)
            {
                target = matches[0];
            }
            else if (matches.Count > 1)
            {
                MarkUnavailable(sessionId, SessionLogStreamIds.AppleUnifiedLog, "More than one simulator matched the session process.");
                return;
            }
        }

        if (target is null
            || !await client.ProcessExistsAsync(target.Udid, processId.Value, cancellationToken).ConfigureAwait(false))
        {
            MarkUnavailable(sessionId, SessionLogStreamIds.AppleUnifiedLog, "No booted simulator matched the session device and process.");
            return;
        }

        runtimeState.EnsureSessionLogStream(
            sessionId,
            new SessionLogStream
            {
                StreamId = SessionLogStreamIds.AppleUnifiedLog,
                Kind = SessionLogStreamKinds.AppleUnifiedLog,
                DisplayName = "Apple Unified Log",
                Status = SessionLogStreamStatuses.Active,
                StartedUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["deviceUdid"] = target.Udid,
                    ["deviceName"] = target.Name,
                    ["processId"] = processId.Value.ToString(CultureInfo.InvariantCulture),
                    ["developerDirectory"] = tool.DeveloperDirectory
                }
            });

        await using var stream = await client.StartLogStreamAsync(
            new SimCtlLogStreamRequest(target.Udid, processId.Value),
            cancellationToken).ConfigureAwait(false);
        var entryChannel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(mappedEntryBufferCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        var writerTask = WriteBatchesAsync(
            sessionId,
            SessionLogStreamIds.AppleUnifiedLog,
            entryChannel.Reader,
            cancellationToken);
        try
        {
            await foreach (var entry in stream.ReadEntriesAsync(cancellationToken).ConfigureAwait(false))
            {
                await entryChannel.Writer.WriteAsync(MapSimCtlEntry(entry), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            entryChannel.Writer.TryComplete();
            await AwaitCaptureTaskAsync(writerTask).ConfigureAwait(false);
        }
    }

    private static string? ResolveNativeDeviceId(string? profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(profileJson);
            var root = document.RootElement;
            if (root.TryGetProperty("device", out var device)
                && device.ValueKind == JsonValueKind.Object)
            {
                return JsonUtil.TryGetString(device, "nativeDeviceId")?.Trim();
            }
        }
        catch (JsonException)
        {
            // This exception is an expected fallback for the best-effort operation.
        }

        return null;
    }

    private async Task WriteBatchesAsync(
        string sessionId,
        string streamId,
        ChannelReader<LogEntry> reader,
        CancellationToken cancellationToken)
    {
        var batch = new List<LogEntry>(maximumBatchSize);
        using var timer = new PeriodicTimer(batchWindow);
        var entriesAvailableTask = reader.WaitToReadAsync(cancellationToken).AsTask();
        var timerTask = timer.WaitForNextTickAsync(cancellationToken).AsTask();
        while (true)
        {
            var completedTask = await Task.WhenAny(entriesAvailableTask, timerTask).ConfigureAwait(false);
            if (completedTask == entriesAvailableTask)
            {
                if (!await entriesAvailableTask.ConfigureAwait(false))
                {
                    FlushBatch();
                    return;
                }

                while (batch.Count < maximumBatchSize && reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                if (batch.Count == maximumBatchSize)
                {
                    FlushBatch();
                }

                entriesAvailableTask = reader.WaitToReadAsync(cancellationToken).AsTask();
            }

            if (completedTask == timerTask)
            {
                if (!await timerTask.ConfigureAwait(false))
                {
                    FlushBatch();
                    return;
                }

                FlushBatch();
                timerTask = timer.WaitForNextTickAsync(cancellationToken).AsTask();
            }
        }

        void FlushBatch()
        {
            if (batch.Count > 0)
            {
                runtimeState.AddSessionLogEntries(sessionId, streamId, batch.ToArray());
                batch.Clear();
            }
        }
    }

    private async Task PumpAdbEntriesAsync(
        AdbLogcatStream stream,
        ChannelWriter<LogEntry> writer,
        DateTimeOffset notBeforeUtc,
        CancellationToken cancellationToken)
    {
        await foreach (var entry in stream.ReadEntriesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (entry.TimestampUtc < notBeforeUtc)
            {
                continue;
            }

            await writer.WriteAsync(MapAdbEntry(entry), cancellationToken).ConfigureAwait(false);
        }
    }

    private LogEntry MapAdbEntry(AdbLogEntry entry)
        => new(entry.TimestampUtc, entry.Message)
        {
            StreamId = SessionLogStreamIds.AndroidLogcat,
            Source = "Android",
            Tag = stringPool.Get(entry.Tag),
            EventId = CreateAdbEventId(entry),
            Priority = entry.Priority switch
            {
                AdbLogPriority.Verbose => LogPriority.Verbose,
                AdbLogPriority.Debug => LogPriority.Debug,
                AdbLogPriority.Information => LogPriority.Information,
                AdbLogPriority.Warning => LogPriority.Warning,
                AdbLogPriority.Error => LogPriority.Error,
                AdbLogPriority.Fatal => LogPriority.Fatal,
                _ => LogPriority.Unknown
            },
            ProcessId = entry.ProcessId,
            ThreadId = entry.ThreadId
        };

    private LogEntry MapSimCtlEntry(SimCtlLogEntry entry)
        => new(entry.TimestampUtc, entry.Message)
        {
            StreamId = SessionLogStreamIds.AppleUnifiedLog,
            Source = string.IsNullOrWhiteSpace(entry.Subsystem) ? "Apple" : stringPool.Get(entry.Subsystem),
            Tag = stringPool.Get(string.IsNullOrWhiteSpace(entry.Category)
                ? Path.GetFileName(entry.ProcessImagePath)
                : entry.Category),
            Priority = entry.Priority switch
            {
                SimCtlLogPriority.Debug => LogPriority.Debug,
                SimCtlLogPriority.Information or SimCtlLogPriority.Default => LogPriority.Information,
                SimCtlLogPriority.Error => LogPriority.Error,
                SimCtlLogPriority.Fault => LogPriority.Fatal,
                _ => LogPriority.Unknown
            },
            ProcessId = entry.ProcessId,
            ThreadId = entry.ThreadId is >= int.MinValue and <= int.MaxValue ? (int)entry.ThreadId : null
        };

    private static string CreateAdbEventId(AdbLogEntry entry)
    {
        const ulong offsetBasis = 14695981039346656037;
        var hash = offsetBasis;
        AddHashValue(ref hash, entry.TimestampUtc.UtcTicks);
        AddHashValue(ref hash, entry.ProcessId);
        AddHashValue(ref hash, entry.ThreadId);
        AddHashValue(ref hash, entry.Tag);
        AddHashValue(ref hash, entry.Message);
        return $"native:adb:{hash:x16}";
    }

    private static void AddHashValue(ref ulong hash, long value)
    {
        unchecked
        {
            for (var index = 0; index < sizeof(long); index++)
            {
                hash ^= (byte)value;
                hash *= 1099511628211;
                value >>= 8;
            }

            hash ^= byte.MaxValue;
            hash *= 1099511628211;
        }
    }

    private static void AddHashValue(ref ulong hash, string value)
    {
        unchecked
        {
            foreach (var character in value)
            {
                hash ^= (byte)character;
                hash *= 1099511628211;
                hash ^= (byte)(character >> 8);
                hash *= 1099511628211;
            }

            hash ^= byte.MaxValue;
            hash *= 1099511628211;
        }
    }

    private static SessionLogStream CreatePendingStream(
        string streamId,
        string kind,
        string displayName,
        string? appIdentifier,
        int? processId)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(appIdentifier))
        {
            metadata["appIdentifier"] = appIdentifier.Trim();
        }

        if (processId.HasValue)
        {
            metadata["processId"] = processId.Value.ToString(CultureInfo.InvariantCulture);
        }

        return new SessionLogStream
        {
            StreamId = streamId,
            Kind = kind,
            DisplayName = displayName,
            Status = SessionLogStreamStatuses.Pending,
            Metadata = metadata
        };
    }

    private void MarkUnavailable(string sessionId, string streamId, string message)
    {
        runtimeState.SetSessionLogStreamStatus(
            sessionId,
            streamId,
            SessionLogStreamStatuses.Unavailable,
            message,
            DateTimeOffset.UtcNow);
        log.Info($"native_log_capture_unavailable sessionId={sessionId} streamId={streamId} reason=\"{message}\"");
    }

    private static bool IsNativeStream(string streamId)
        => string.Equals(streamId, SessionLogStreamIds.AndroidLogcat, StringComparison.Ordinal)
           || string.Equals(streamId, SessionLogStreamIds.AppleUnifiedLog, StringComparison.Ordinal);

    private void MarkActiveNativeStreamCompleted(string sessionId, string osName, string message)
    {
        var streamId = string.Equals(osName, "android", StringComparison.Ordinal)
            ? SessionLogStreamIds.AndroidLogcat
            : SessionLogStreamIds.AppleUnifiedLog;
        if (!runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot)
            || snapshot?.LogStreams.FirstOrDefault(stream => string.Equals(stream.StreamId, streamId, StringComparison.Ordinal))?.Status
            != SessionLogStreamStatuses.Active)
        {
            return;
        }

        runtimeState.SetSessionLogStreamStatus(
            sessionId,
            streamId,
            SessionLogStreamStatuses.Completed,
            message,
            DateTimeOffset.UtcNow);
    }

    private static async Task AwaitCaptureTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected completion path.
        }
    }

    private sealed class NativeCaptureHandle
    {
        public NativeCaptureHandle(CancellationTokenSource cancellation)
        {
            Cancellation = cancellation;
        }

        public CancellationTokenSource Cancellation { get; }
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private sealed record AndroidTarget(AdbDevice Device, IReadOnlyList<int> ProcessIds);

    private sealed class AndroidProcessCapture : IAsyncDisposable
    {
        public AndroidProcessCapture(AdbLogcatStream stream, Task pumpTask)
        {
            Stream = stream;
            PumpTask = pumpTask;
        }

        public AdbLogcatStream Stream { get; }
        public Task PumpTask { get; }

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
            await AwaitCaptureTaskAsync(PumpTask).ConfigureAwait(false);
        }
    }

    private sealed class NativeLogStringPool
    {
        private readonly int capacity;
        private readonly Lock gate = new();
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public NativeLogStringPool(int capacity)
        {
            this.capacity = Math.Max(1, capacity);
        }

        public string Get(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            lock (gate)
            {
                if (values.TryGetValue(value, out var pooledValue))
                {
                    return pooledValue;
                }

                if (values.Count < capacity)
                {
                    values[value] = value;
                }

                return value;
            }
        }
    }
}
