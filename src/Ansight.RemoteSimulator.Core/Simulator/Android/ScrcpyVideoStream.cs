using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Ansight.Adb;

namespace Ansight.RemoteSimulator.Core.Simulator.Android;

public sealed class ScrcpyVideoStream : IAsyncDisposable
{
    private const uint H264CodecIdentifier = 0x68323634;
    private const ulong ConfigurationPacketFlag = 1UL << 63;
    private const ulong KeyFramePacketFlag = 1UL << 62;
    private const ulong PresentationTimestampMask = KeyFramePacketFlag - 1;
    private const int MaximumPacketSize = 16 * 1024 * 1024;
    private readonly AdbClient adbClient;
    private readonly ScrcpyToolResolution toolResolution;
    private readonly string deviceSerial;
    private readonly int framesPerSecond;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private IAdbProcess? serverProcess;
    private Task<string>? serverOutputTask;
    private Task<string>? serverErrorTask;
    private TcpClient? videoClient;
    private int forwardedPort;
    private bool disposed;

    public ScrcpyVideoStream(
        AdbClient adbClient,
        ScrcpyToolResolution toolResolution,
        string deviceSerial,
        int framesPerSecond)
    {
        this.adbClient = adbClient ?? throw new ArgumentNullException(nameof(adbClient));
        this.toolResolution = toolResolution ?? throw new ArgumentNullException(nameof(toolResolution));
        if (!toolResolution.IsFound)
        {
            throw new ArgumentException(toolResolution.Message, nameof(toolResolution));
        }

        if (string.IsNullOrWhiteSpace(deviceSerial))
        {
            throw new ArgumentException("An Android device serial is required.", nameof(deviceSerial));
        }

        if (framesPerSecond is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        this.deviceSerial = deviceSerial;
        this.framesPerSecond = framesPerSecond;
    }

    public async Task RunAsync(
        Func<ScrcpyVideoPacket, CancellationToken, Task> receivePacketAsync,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(receivePacketAsync);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token,
            cancellationToken);
        var activeCancellation = linkedCancellation.Token;

        try
        {
            var socketIdentifier = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            var socketName = $"scrcpy_{socketIdentifier:x8}";
            var remoteServerPath = $"/data/local/tmp/ansight-scrcpy-server-{SanitizeVersion(toolResolution.Version)}.jar";
            await PushServerAsync(remoteServerPath, activeCancellation).ConfigureAwait(false);
            forwardedPort = await CreateForwardAsync(socketName, activeCancellation).ConfigureAwait(false);
            StartServer(remoteServerPath, socketIdentifier);
            var connection = await ConnectVideoSocketAsync(forwardedPort, activeCancellation).ConfigureAwait(false);
            videoClient = connection.Client;
            videoClient.NoDelay = true;
            await ReadVideoAsync(
                videoClient.GetStream(),
                connection.CodecHeader,
                receivePacketAsync,
                activeCancellation).ConfigureAwait(false);

            if (!activeCancellation.IsCancellationRequested)
            {
                throw new InvalidOperationException(await BuildUnexpectedEndMessageAsync().ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException) when (activeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            serverProcess?.Terminate(force: true);
            if (serverProcess is not null)
            {
                try
                {
                    using var exitCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await serverProcess.Completion
                        .WaitAsync(exitCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exitError) when (exitError is OperationCanceledException or InvalidOperationException)
                {
                }
            }
            var diagnostic = await ReadServerDiagnosticAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(diagnostic)
                    ? $"The scrcpy H.264 transport failed: {ex.Message}"
                    : $"The scrcpy H.264 transport failed: {ex.Message} scrcpy server: {diagnostic}",
                ex);
        }
        finally
        {
            await StopTransportAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await lifetimeCancellation.CancelAsync().ConfigureAwait(false);
        await StopTransportAsync().ConfigureAwait(false);
        lifetimeCancellation.Dispose();
    }

    internal static async Task<ScrcpyVideoPacketData?> ReadPacketAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[12];
        if (!await TryReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var presentationTimestampAndFlags = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0, 8));
        var packetSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        if (packetSize is 0 or > MaximumPacketSize)
        {
            throw new InvalidDataException($"scrcpy sent an invalid H.264 packet size ({packetSize}).");
        }

        var data = new byte[packetSize];
        await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        return new ScrcpyVideoPacketData(
            data,
            (presentationTimestampAndFlags & ConfigurationPacketFlag) != 0,
            (presentationTimestampAndFlags & KeyFramePacketFlag) != 0,
            checked((long)(presentationTimestampAndFlags & PresentationTimestampMask)));
    }

    private async Task PushServerAsync(string remoteServerPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(toolResolution.ServerPath))
        {
            throw new FileNotFoundException(
                "The validated scrcpy server disappeared before Android video could start.",
                toolResolution.ServerPath);
        }

        var result = await adbClient.RunAsync(
            ["-s", deviceSerial, "push", "--sync", toolResolution.ServerPath, remoteServerPath],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(BuildAdbFailure("install the matching scrcpy server", result));
        }
    }

    private async Task<int> CreateForwardAsync(string socketName, CancellationToken cancellationToken)
    {
        var result = await adbClient.RunAsync(
            ["-s", deviceSerial, "forward", "tcp:0", $"localabstract:{socketName}"],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(BuildAdbFailure("create the scrcpy video tunnel", result));
        }

        if (!int.TryParse(
                result.StandardOutput.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port)
            || port is <= 0 or > 65535)
        {
            throw new InvalidOperationException(
                $"ADB returned an invalid local port for the scrcpy video tunnel: '{result.StandardOutput.Trim()}'.");
        }

        return port;
    }

    private void StartServer(string remoteServerPath, int socketIdentifier)
    {
        serverProcess = adbClient.StartProcess(
        [
            "-s", deviceSerial,
            "shell",
            $"CLASSPATH={remoteServerPath}",
            "app_process", "/", "com.genymobile.scrcpy.Server", toolResolution.Version,
            $"scid={socketIdentifier:x8}",
            "log_level=warn",
            "audio=false",
            "control=false",
            "tunnel_forward=true",
            "send_device_meta=false",
            "send_dummy_byte=false",
            "cleanup=false",
            "video_codec=h264",
            // Independent frames let the browser recover immediately from
            // negotiation or packet loss without waiting for an emulator GOP.
            "video_codec_options=i-frame-interval=0",
            "video_bit_rate=4000000",
            "max_size=1024",
            $"max_fps={framesPerSecond}",
        ]);
        serverProcess.StandardInput.Dispose();
        serverOutputTask = ReadTextAsync(serverProcess.StandardOutput);
        serverErrorTask = ReadTextAsync(serverProcess.StandardError);
    }

    private async Task<ScrcpyVideoConnection> ConnectVideoSocketAsync(
        int port,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (serverProcess?.Completion.IsCompleted == true)
            {
                throw new InvalidOperationException(await BuildServerStartFailureAsync().ConfigureAwait(false));
            }

            var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
                var codecHeader = new byte[12];
                if (await TryReadExactlyAsync(
                        client.GetStream(),
                        codecHeader,
                        cancellationToken).ConfigureAwait(false))
                {
                    return new ScrcpyVideoConnection(client, codecHeader);
                }

                lastError = new EndOfStreamException(
                    "The ADB tunnel accepted before the scrcpy device socket was ready.");
                client.Dispose();
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException)
            {
                lastError = ex;
                client.Dispose();
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException(
            $"Timed out connecting to the scrcpy video tunnel on port {port}: {lastError?.Message}");
    }

    internal static async Task ReadVideoAsync(
        Stream stream,
        byte[] codecHeader,
        Func<ScrcpyVideoPacket, CancellationToken, Task> receivePacketAsync,
        CancellationToken cancellationToken)
    {
        var codecIdentifier = BinaryPrimitives.ReadUInt32BigEndian(codecHeader.AsSpan(0, 4));
        var width = BinaryPrimitives.ReadUInt32BigEndian(codecHeader.AsSpan(4, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(codecHeader.AsSpan(8, 4));
        if (codecIdentifier != H264CodecIdentifier)
        {
            throw new InvalidDataException($"scrcpy selected unsupported video codec 0x{codecIdentifier:x8}; H.264 is required.");
        }

        if (width is 0 or > 16384 || height is 0 or > 16384)
        {
            throw new InvalidDataException($"scrcpy reported an invalid video size ({width}x{height}).");
        }

        byte[]? configurationPacket = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var packet = await ReadPacketAsync(stream, cancellationToken).ConfigureAwait(false);
            if (packet is null)
            {
                return;
            }

            if (packet.IsConfiguration)
            {
                configurationPacket = packet.Data;
                continue;
            }

            // A recovering WebRTC decoder needs SPS/PPS with its next IDR,
            // not just with the first frame emitted before negotiation.
            var accessUnit = configurationPacket is null || !packet.IsKeyFrame
                ? packet.Data
                : Combine(configurationPacket, packet.Data);
            await receivePacketAsync(
                new ScrcpyVideoPacket(
                    accessUnit,
                    packet.PresentationTimestampMicroseconds,
                    packet.IsKeyFrame,
                    checked((int)width),
                    checked((int)height)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StopTransportAsync()
    {
        videoClient?.Dispose();
        videoClient = null;
        var activeProcess = serverProcess;
        serverProcess = null;
        if (activeProcess is not null)
        {
            activeProcess.Terminate(force: true);
            try
            {
                await activeProcess.Completion.ConfigureAwait(false);
            }
            catch (Exception suppressedException)
            {
                System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
            }
            try
            {
                await Task.WhenAll(
                        serverOutputTask ?? Task.FromResult(string.Empty),
                        serverErrorTask ?? Task.FromResult(string.Empty))
                    .ConfigureAwait(false);
            }
            catch (Exception suppressedException)
            {
                System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
            }
            await activeProcess.DisposeAsync().ConfigureAwait(false);
        }
        serverOutputTask = null;
        serverErrorTask = null;

        if (forwardedPort > 0)
        {
            var port = forwardedPort;
            forwardedPort = 0;
            try
            {
                using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await adbClient.RunAsync(
                    ["-s", deviceSerial, "forward", "--remove", $"tcp:{port}"],
                    cleanupCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
        }
    }

    private async Task<string> BuildServerStartFailureAsync()
    {
        var detail = await ReadServerDiagnosticAsync().ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(detail)
            ? $"scrcpy {toolResolution.Version} exited before its video tunnel became ready."
            : $"scrcpy {toolResolution.Version} failed to start: {detail}";
    }

    private async Task<string> BuildUnexpectedEndMessageAsync()
    {
        var detail = await ReadServerDiagnosticAsync().ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(detail)
            ? "The scrcpy H.264 video stream ended unexpectedly."
            : $"The scrcpy H.264 video stream ended unexpectedly: {detail}";
    }

    private async Task<string> ReadServerDiagnosticAsync()
    {
        if (serverProcess is not null && !serverProcess.Completion.IsCompleted)
        {
            return string.Empty;
        }

        var error = serverErrorTask is null ? string.Empty : await serverErrorTask.ConfigureAwait(false);
        var output = serverOutputTask is null ? string.Empty : await serverOutputTask.ConfigureAwait(false);
        var detail = string.IsNullOrWhiteSpace(error) ? output : error;
        return CollapseWhitespace(detail);
    }

    private static async Task<bool> TryReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return offset == 0 ? false : throw new EndOfStreamException("The scrcpy packet header ended unexpectedly.");
            }
            offset += read;
        }
        return true;
    }

    private static byte[] Combine(byte[] prefix, byte[] suffix)
    {
        var combined = new byte[checked(prefix.Length + suffix.Length)];
        prefix.CopyTo(combined, 0);
        suffix.CopyTo(combined, prefix.Length);
        return combined;
    }

    private static string BuildAdbFailure(string operation, AdbCommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        return string.IsNullOrWhiteSpace(detail)
            ? $"ADB failed to {operation} with exit code {result.ExitCode}."
            : $"ADB failed to {operation}: {CollapseWhitespace(detail)}";
    }

    private static string SanitizeVersion(string version)
    {
        var builder = new StringBuilder(version.Length);
        foreach (var character in version)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' ? character : '_');
        }
        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    private static string CollapseWhitespace(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static async Task<string> ReadTextAsync(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }
}
