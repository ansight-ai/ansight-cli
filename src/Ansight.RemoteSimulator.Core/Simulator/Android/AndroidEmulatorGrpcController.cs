using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Ansight.RemoteSimulator.Core.Simulator.Android.Grpc;

namespace Ansight.RemoteSimulator.Core.Simulator.Android;

public sealed class AndroidEmulatorGrpcController : IAndroidEmulatorController, IDisposable
{
    private const int MaximumGrpcMessageLength = 32 * 1024 * 1024;
    private const int RequestedFrameWidth = 720;
    private const int RequestedFrameHeight = 1440;
    private const string ServicePath = "/android.emulation.control.EmulatorController";
    private readonly ConcurrentDictionary<string, AndroidEmulatorFrameStream> frameStreams =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, HttpClient> clients = [];
    private bool disposed;

    public async Task<byte[]> CaptureScreenshotPngAsync(
        string deviceSerial,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSerial);

        var stream = frameStreams.GetOrAdd(
            deviceSerial,
            serial => new AndroidEmulatorFrameStream(
                GetClient(Endpoint.Resolve(serial)),
                CreateImageFormatRequest(),
                MaximumGrpcMessageLength));
        try
        {
            return await stream.GetLatestFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (frameStreams.TryRemove(deviceSerial, out var failedStream))
            {
                failedStream.Dispose();
            }

            throw;
        }
    }

    public async Task SendTouchAsync(
        string deviceSerial,
        IReadOnlyList<AndroidEmulatorTouch> touches,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSerial);
        ArgumentNullException.ThrowIfNull(touches);
        if (touches.Count == 0)
        {
            throw new ArgumentException("At least one Android touch contact is required.", nameof(touches));
        }

        var client = GetClient(Endpoint.Resolve(deviceSerial));
        using var request = CreateGrpcRequest(
            $"{ServicePath}/sendTouch",
            MessageCodec.EncodeTouchEvent(touches));
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        ValidateUnaryResponse(response, responseBytes);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var stream in frameStreams.Values)
        {
            stream.Dispose();
        }
        frameStreams.Clear();
        foreach (var client in clients.Values)
        {
            client.Dispose();
        }
        clients.Clear();
    }

    private HttpClient GetClient(Endpoint endpoint)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return clients.GetOrAdd(endpoint.Port, static port =>
        {
            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(1),
            };
            return new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = Timeout.InfiniteTimeSpan,
            };
        });
    }

    private static HttpRequestMessage CreateGrpcRequest(string path, byte[] message)
    {
        var framedMessage = new byte[message.Length + 5];
        BinaryPrimitives.WriteInt32BigEndian(framedMessage.AsSpan(1, 4), message.Length);
        message.CopyTo(framedMessage.AsSpan(5));

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(framedMessage),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        request.Headers.TryAddWithoutValidation("te", "trailers");
        request.Headers.TryAddWithoutValidation("grpc-accept-encoding", "identity");
        return request;
    }

    private static byte[] CreateImageFormatRequest()
        => MessageCodec.EncodeImageFormat(
            RequestedFrameWidth,
            RequestedFrameHeight);

    private static void ValidateUnaryResponse(HttpResponseMessage response, byte[] responseBytes)
    {
        var grpcStatus = ReadGrpcHeader(response, "grpc-status");
        if (!string.IsNullOrWhiteSpace(grpcStatus) && grpcStatus != "0")
        {
            var message = ReadGrpcHeader(response, "grpc-message");
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message)
                    ? $"Android Emulator gRPC returned status {grpcStatus}."
                    : Uri.UnescapeDataString(message));
        }

        if (responseBytes.Length < 5 || responseBytes[0] != 0)
        {
            throw new InvalidOperationException("Android Emulator gRPC returned an invalid unary response.");
        }

        var messageLength = BinaryPrimitives.ReadInt32BigEndian(responseBytes.AsSpan(1, 4));
        if (messageLength < 0 || responseBytes.Length != messageLength + 5)
        {
            throw new InvalidOperationException("Android Emulator gRPC returned a truncated unary response.");
        }
    }

    private static string? ReadGrpcHeader(HttpResponseMessage response, string name)
    {
        if (response.TrailingHeaders.TryGetValues(name, out var trailingValues))
        {
            return trailingValues.FirstOrDefault();
        }

        return response.Headers.TryGetValues(name, out var headerValues)
            ? headerValues.FirstOrDefault()
            : null;
    }

    private sealed class AndroidEmulatorFrameStream : IDisposable
    {
        private readonly HttpClient client;
        private readonly byte[] imageFormatRequest;
        private readonly int maximumMessageLength;
        private readonly CancellationTokenSource cancellation = new();
        private readonly TaskCompletionSource<byte[]> firstFrame = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task pumpTask;
        private byte[]? latestFrame;
        private Exception? failure;
        private bool disposed;

        public AndroidEmulatorFrameStream(
            HttpClient client,
            byte[] imageFormatRequest,
            int maximumMessageLength)
        {
            this.client = client;
            this.imageFormatRequest = imageFormatRequest;
            this.maximumMessageLength = maximumMessageLength;
            pumpTask = PumpAsync(cancellation.Token);
        }

        public async Task<byte[]> GetLatestFrameAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var streamFailure = Volatile.Read(ref failure);
            if (streamFailure is not null)
            {
                throw new InvalidOperationException(
                    "Android Emulator screenshot streaming is unavailable.",
                    streamFailure);
            }

            var current = Volatile.Read(ref latestFrame);
            if (current is not null)
            {
                return current;
            }

            return await firstFrame.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            cancellation.Cancel();
            cancellation.Dispose();
            _ = pumpTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var request = CreateGrpcRequest(
                    $"{ServicePath}/streamScreenshot",
                    imageFormatRequest);
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var content = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                var header = new byte[5];
                while (true)
                {
                    var hasHeader = await TryReadExactlyAsync(content, header, cancellationToken).ConfigureAwait(false);
                    if (!hasHeader)
                    {
                        break;
                    }

                    if (header[0] != 0)
                    {
                        throw new InvalidOperationException("Compressed Android Emulator gRPC frames are unsupported.");
                    }

                    var messageLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
                    if (messageLength <= 0 || messageLength > maximumMessageLength)
                    {
                        throw new InvalidOperationException("Android Emulator gRPC returned an invalid frame length.");
                    }

                    var message = new byte[messageLength];
                    await content.ReadExactlyAsync(message, cancellationToken).ConfigureAwait(false);
                    var png = MessageCodec.DecodeImagePng(message);
                    if (png.Length == 0)
                    {
                        continue;
                    }

                    Volatile.Write(ref latestFrame, png);
                    firstFrame.TrySetResult(png);
                }

                var grpcStatus = ReadGrpcHeader(response, "grpc-status");
                if (!string.IsNullOrWhiteSpace(grpcStatus) && grpcStatus != "0")
                {
                    throw new InvalidOperationException(
                        $"Android Emulator screenshot streaming stopped with gRPC status {grpcStatus}.");
                }

                throw new EndOfStreamException("Android Emulator screenshot streaming ended.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                firstFrame.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref failure, ex);
                firstFrame.TrySetException(ex);
                throw;
            }
        }

        private static async Task<bool> TryReadExactlyAsync(
            Stream stream,
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < destination.Length)
            {
                var read = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return offset == 0
                        ? false
                        : throw new EndOfStreamException("Android Emulator gRPC returned a partial frame header.");
                }

                offset += read;
            }

            return true;
        }
    }
}
