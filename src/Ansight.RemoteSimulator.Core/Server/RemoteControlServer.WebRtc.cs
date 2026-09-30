using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ansight.RemoteSimulator.Core.AppInspection;
using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Server.WebRtc;
using Ansight.RemoteSimulator.Core.WebRtc;
using static Ansight.RemoteSimulator.Core.Server.RemoteControlHttpTransport;

namespace Ansight.RemoteSimulator.Core.Server;

public sealed partial class RemoteControlServer
{
    private bool IsBootedDevice(string deviceUdid)
        => runtimeSource.Current.Devices.Any(device =>
            device.IsBooted && string.Equals(device.Identifier, deviceUdid, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> AuthorizeAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue("Authorization", out var authorization)
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var bearerToken = authorization["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return false;
        }

        var result = await accessAuthorizer!
            .AuthorizeAsync(bearerToken, cancellationToken)
            .ConfigureAwait(false);
        return result.IsAuthorized;
    }

    private static bool IsLoopback(TcpClient client)
        => client.Client.RemoteEndPoint is IPEndPoint remoteEndPoint
           && IPAddress.IsLoopback(remoteEndPoint.Address);

    private async Task<OfferResponse> CreateWebRtcAnswerAsync(
        string deviceUdid,
        int framesPerSecond,
        WebRtcSessionDescription browserOffer,
        IReadOnlySet<string> grantedScopes,
        CancellationToken cancellationToken)
    {
        var session = webRtcSessionFactory!.Create(deviceUdid, framesPerSecond);
        var sessionId = CreateSessionToken();
        var inputDispatcher = new InputDispatcher(
            message => DeliverWebRtcInputAsync(sessionId, message));
        EventHandler<WebRtcInputMessageEventArgs>? inputHandler = null;
        inputHandler = (_, args) => inputDispatcher.Enqueue(args.Message);
        var registration = new SessionRegistration(
            session,
            inputHandler,
            inputDispatcher,
            grantedScopes);
        session.InputReceived += inputHandler;
        if (!webRtcSessions.TryAdd(sessionId, registration))
        {
            session.InputReceived -= inputHandler;
            await inputDispatcher.DisposeAsync().ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Could not allocate a WebRTC session identifier.");
        }

        try
        {
            var answer = await session
                .CreateAnswerAsync(browserOffer, cancellationToken)
                .ConfigureAwait(false);
            return new OfferResponse(
                sessionId,
                answer.Type,
                TerminateSdp(answer.Sdp),
                framesPerSecond,
                grantedScopes.OrderBy(scope => scope, StringComparer.Ordinal).ToArray());
        }
        catch
        {
            await CloseWebRtcSessionAsync(sessionId).ConfigureAwait(false);
            throw;
        }
    }

    internal static string TerminateSdp(string sdp)
        => sdp.EndsWith("\r\n", StringComparison.Ordinal)
            ? sdp
            : $"{sdp.TrimEnd('\r', '\n')}\r\n";

    private async Task DeliverWebRtcInputAsync(string sessionId, string message)
    {
        try
        {
            if (message.Length > MaximumRequestBodyBytes
                || !webRtcSessions.TryGetValue(sessionId, out var registration))
            {
                return;
            }

            using var document = JsonDocument.Parse(message);
            if (document.RootElement.TryGetProperty("kind", out var kind)
                && string.Equals(kind.GetString(), "companion_presence", StringComparison.Ordinal))
            {
                ApplyCompanionPresence(sessionId, registration, document.RootElement);
                return;
            }
            if (document.RootElement.TryGetProperty("kind", out kind)
                && string.Equals(kind.GetString(), "recording_request", StringComparison.Ordinal))
            {
                await DeliverWebRtcRecordingRequestAsync(
                    registration.Session,
                    registration.GrantedScopes,
                    document.RootElement).ConfigureAwait(false);
                return;
            }
            if (document.RootElement.TryGetProperty("kind", out kind)
                && string.Equals(kind.GetString(), "inspection_request", StringComparison.Ordinal))
            {
                await DeliverWebRtcInspectionRequestAsync(
                    registration.Session,
                    registration.GrantedScopes,
                    document.RootElement).ConfigureAwait(false);
                return;
            }
            if (document.RootElement.TryGetProperty("button", out _))
            {
                var buttonEvent = document.RootElement.Deserialize<RemoteButtonEvent>(JsonOptions)
                    ?.NormalizeAndValidate();
                if (buttonEvent is null
                    || !IsMatchingBootedDevice(buttonEvent.DeviceUdid, registration.Session.DeviceUdid))
                {
                    return;
                }

                await inputSink.SendButtonAsync(buttonEvent).ConfigureAwait(false);
                return;
            }

            if (document.RootElement.TryGetProperty("usageCode", out _))
            {
                var keyEvent = document.RootElement.Deserialize<RemoteKeyEvent>(JsonOptions)
                    ?.NormalizeAndValidate();
                if (keyEvent is null
                    || !IsMatchingBootedDevice(keyEvent.DeviceUdid, registration.Session.DeviceUdid))
                {
                    return;
                }

                await inputSink.SendKeyAsync(keyEvent).ConfigureAwait(false);
                return;
            }

            if (document.RootElement.TryGetProperty("text", out _))
            {
                var textEvent = document.RootElement.Deserialize<RemoteTextEvent>(JsonOptions)
                    ?.NormalizeAndValidate();
                if (textEvent is null
                    || !IsMatchingBootedDevice(textEvent.DeviceUdid, registration.Session.DeviceUdid))
                {
                    return;
                }

                await inputSink.SendTextAsync(textEvent).ConfigureAwait(false);
                return;
            }

            var pointerEvent = document.RootElement.Deserialize<RemotePointerEvent>(JsonOptions)
                ?.NormalizeAndValidate();
            if (pointerEvent is null
                || !IsMatchingBootedDevice(pointerEvent.DeviceUdid, registration.Session.DeviceUdid))
            {
                return;
            }

            await inputSink.SendAsync(pointerEvent).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A malformed or stale data-channel message must not tear down the media session.
            System.Diagnostics.Trace.TraceWarning(exception.ToString());
        }
    }

    private async Task DeliverWebRtcInspectionRequestAsync(
        ISimulatorWebRtcSession session,
        IReadOnlySet<string> grantedScopes,
        JsonElement payload)
    {
        var request = payload.Deserialize<InspectionRequest>(JsonOptions);
        if (request is null
            || string.IsNullOrWhiteSpace(request.RequestId)
            || request.RequestId.Length > 128)
        {
            return;
        }

        if (!grantedScopes.Contains(InspectionReadScope))
        {
            await SendInspectionFailureAsync(
                session,
                request.RequestId,
                HttpStatusCode.Forbidden,
                "Live app inspection requires the inspect.read scope.").ConfigureAwait(false);
            return;
        }

        if (appInspectionSource is null)
        {
            await SendInspectionFailureAsync(
                session,
                request.RequestId,
                HttpStatusCode.ServiceUnavailable,
                "Live app inspection is unavailable on this host.").ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await appInspectionSource.InvokeAsync(
                session.DeviceUdid,
                new RemoteAppInspectionRequest(
                    request.RequestId,
                    request.Operation,
                    request.SessionId,
                    request.Arguments)).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                await SendInspectionFailureAsync(
                    session,
                    request.RequestId,
                    result.StatusCode,
                    result.Error ?? "The Ansight host rejected the inspection request.").ConfigureAwait(false);
                return;
            }

            await SendInspectionResponseAsync(
                session,
                request.RequestId,
                result.StatusCode,
                result.ContentType,
                result.Content).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await SendInspectionFailureAsync(
                session,
                request.RequestId,
                HttpStatusCode.InternalServerError,
                exception.Message).ConfigureAwait(false);
        }
    }

    private async Task SendInspectionResponseAsync(
        ISimulatorWebRtcSession session,
        string requestId,
        HttpStatusCode statusCode,
        string contentType,
        byte[] content)
    {
        if (content.Length > MaximumRecordingResponseBytes)
        {
            await SendInspectionFailureAsync(
                session,
                requestId,
                HttpStatusCode.RequestEntityTooLarge,
                "The inspection response is too large for remote review.").ConfigureAwait(false);
            return;
        }

        var chunkCount = Math.Max(
            1,
            (int)Math.Ceiling(content.Length / (double)RecordingResponseChunkBytes));
        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var offset = chunkIndex * RecordingResponseChunkBytes;
            var length = Math.Min(RecordingResponseChunkBytes, content.Length - offset);
            var data = length == 0
                ? string.Empty
                : Convert.ToBase64String(content, offset, length);
            var message = JsonSerializer.Serialize(
                new InspectionResponse(
                    "inspection_response",
                    requestId,
                    (int)statusCode,
                    contentType,
                    chunkIndex,
                    chunkCount,
                    data,
                    null),
                JsonOptions);
            if (!await SendDataChannelMessageAsync(session, message).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task SendInspectionFailureAsync(
        ISimulatorWebRtcSession session,
        string requestId,
        HttpStatusCode statusCode,
        string error)
    {
        var message = JsonSerializer.Serialize(
            new InspectionResponse(
                "inspection_response",
                requestId,
                (int)statusCode,
                "application/json",
                0,
                0,
                string.Empty,
                error),
            JsonOptions);
        await SendDataChannelMessageAsync(session, message).ConfigureAwait(false);
    }

    private async Task DeliverWebRtcRecordingRequestAsync(
        ISimulatorWebRtcSession session,
        IReadOnlySet<string> grantedScopes,
        JsonElement payload)
    {
        var request = payload.Deserialize<RecordingRequest>(JsonOptions);
        if (request is null
            || string.IsNullOrWhiteSpace(request.RequestId)
            || request.RequestId.Length > 128)
        {
            return;
        }

        try
        {
            if (string.Equals(request.Operation, "live_frame", StringComparison.Ordinal))
            {
                var liveFrame = await frameSource.CaptureAsync(session.DeviceUdid).ConfigureAwait(false);
                await SendRecordingResponseAsync(
                    session,
                    request.RequestId,
                    HttpStatusCode.OK,
                    liveFrame.ContentType,
                    liveFrame.Content).ConfigureAwait(false);
                return;
            }

            if (!grantedScopes.Contains(RecordingReadScope))
            {
                await SendRecordingFailureAsync(
                    session,
                    request.RequestId,
                    HttpStatusCode.Forbidden,
                    "Recording review requires the recordings.read scope.").ConfigureAwait(false);
                return;
            }

            if (recordingSource is null)
            {
                await SendRecordingFailureAsync(
                    session,
                    request.RequestId,
                    HttpStatusCode.ServiceUnavailable,
                    "Recording review is unavailable on this host.").ConfigureAwait(false);
                return;
            }

            switch (request.Operation)
            {
                case "list":
                {
                    var recordings = await recordingSource.ListAsync().ConfigureAwait(false);
                    await SendRecordingResponseAsync(
                        session,
                        request.RequestId,
                        HttpStatusCode.OK,
                        "application/json",
                        JsonSerializer.SerializeToUtf8Bytes(recordings, JsonOptions)).ConfigureAwait(false);
                    return;
                }
                case "details" when IsValidRecordingIdentifier(request.RecordingId):
                {
                    var details = await recordingSource
                        .GetAsync(request.RecordingId!)
                        .ConfigureAwait(false);
                    if (details is null)
                    {
                        await SendRecordingFailureAsync(
                            session,
                            request.RequestId,
                            HttpStatusCode.NotFound,
                            "The recording was not found.").ConfigureAwait(false);
                        return;
                    }
                    await SendRecordingResponseAsync(
                        session,
                        request.RequestId,
                        HttpStatusCode.OK,
                        "application/json",
                        JsonSerializer.SerializeToUtf8Bytes(details, JsonOptions)).ConfigureAwait(false);
                    return;
                }
                case "frame" when IsValidRecordingIdentifier(request.RecordingId)
                                  && IsValidRecordingIdentifier(request.FrameId):
                {
                    var frame = await recordingSource
                        .GetFrameAsync(request.RecordingId!, request.FrameId!)
                        .ConfigureAwait(false);
                    if (frame is null)
                    {
                        await SendRecordingFailureAsync(
                            session,
                            request.RequestId,
                            HttpStatusCode.NotFound,
                            "The recording frame was not found.").ConfigureAwait(false);
                        return;
                    }
                    await SendRecordingResponseAsync(
                        session,
                        request.RequestId,
                        HttpStatusCode.OK,
                        frame.ContentType,
                        frame.Content).ConfigureAwait(false);
                    return;
                }
                case "icon" when IsValidRecordingIdentifier(request.RecordingId):
                {
                    var icon = await recordingSource
                        .GetAppIconAsync(request.RecordingId!)
                        .ConfigureAwait(false);
                    if (icon is null)
                    {
                        await SendRecordingFailureAsync(
                            session,
                            request.RequestId,
                            HttpStatusCode.NotFound,
                            "The recording app icon was not found.").ConfigureAwait(false);
                        return;
                    }
                    await SendRecordingResponseAsync(
                        session,
                        request.RequestId,
                        HttpStatusCode.OK,
                        icon.ContentType,
                        icon.Content).ConfigureAwait(false);
                    return;
                }
                case "evidence" when IsValidRecordingIdentifier(request.RecordingId)
                                     && request.StartUtc.HasValue
                                     && request.EndUtc.HasValue
                                     && request.EndUtc.Value >= request.StartUtc.Value:
                {
                    var evidence = await recordingSource
                        .GetEvidenceAsync(
                            request.RecordingId!,
                            request.StartUtc.Value,
                            request.EndUtc.Value,
                            Math.Clamp(request.MaximumLogCount ?? 250, 1, 1_000),
                            Math.Clamp(request.MaximumMetricSampleCount ?? 2_000, 1, 10_000))
                        .ConfigureAwait(false);
                    if (evidence is null)
                    {
                        await SendRecordingFailureAsync(
                            session,
                            request.RequestId,
                            HttpStatusCode.NotFound,
                            "The recording was not found.").ConfigureAwait(false);
                        return;
                    }
                    await SendRecordingResponseAsync(
                        session,
                        request.RequestId,
                        HttpStatusCode.OK,
                        "application/json",
                        JsonSerializer.SerializeToUtf8Bytes(evidence, JsonOptions)).ConfigureAwait(false);
                    return;
                }
                default:
                    await SendRecordingFailureAsync(
                        session,
                        request.RequestId,
                        HttpStatusCode.BadRequest,
                        "The recording request is invalid.").ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            await SendRecordingFailureAsync(
                session,
                request.RequestId,
                HttpStatusCode.InternalServerError,
                ex.Message).ConfigureAwait(false);
        }
    }

    private async Task SendRecordingResponseAsync(
        ISimulatorWebRtcSession session,
        string requestId,
        HttpStatusCode statusCode,
        string contentType,
        byte[] content)
    {
        if (content.Length > MaximumRecordingResponseBytes)
        {
            await SendRecordingFailureAsync(
                session,
                requestId,
                HttpStatusCode.RequestEntityTooLarge,
                "The recording response is too large for remote review.").ConfigureAwait(false);
            return;
        }

        var chunkCount = Math.Max(
            1,
            (int)Math.Ceiling(content.Length / (double)RecordingResponseChunkBytes));
        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var offset = chunkIndex * RecordingResponseChunkBytes;
            var length = Math.Min(RecordingResponseChunkBytes, content.Length - offset);
            var data = length == 0
                ? string.Empty
                : Convert.ToBase64String(content, offset, length);
            var message = JsonSerializer.Serialize(
                new RecordingResponse(
                    "recording_response",
                    requestId,
                    (int)statusCode,
                    contentType,
                    chunkIndex,
                    chunkCount,
                    data,
                    null),
                JsonOptions);
            if (!await SendDataChannelMessageAsync(session, message).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task SendRecordingFailureAsync(
        ISimulatorWebRtcSession session,
        string requestId,
        HttpStatusCode statusCode,
        string error)
    {
        var message = JsonSerializer.Serialize(
            new RecordingResponse(
                "recording_response",
                requestId,
                (int)statusCode,
                "application/json",
                0,
                0,
                string.Empty,
                error),
            JsonOptions);
        await SendDataChannelMessageAsync(session, message).ConfigureAwait(false);
    }

    private async Task<bool> SendDataChannelMessageAsync(
        ISimulatorWebRtcSession session,
        string message)
    {
        var cancellationToken = serverCancellation?.Token ?? CancellationToken.None;
        for (var attempt = 0; attempt < 500 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            if (session.TrySendMessage(message))
            {
                return true;
            }
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static bool IsValidRecordingIdentifier(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;

    private static int ReadBoundedQueryCount(
        IReadOnlyDictionary<string, string> query,
        string name,
        int defaultValue,
        int minimum,
        int maximum)
        => query.TryGetValue(name, out var text) && int.TryParse(text, out var value)
            ? Math.Clamp(value, minimum, maximum)
            : defaultValue;

    private static IReadOnlySet<string> NormalizeScopes(IEnumerable<string>? scopes)
        => new HashSet<string>(
            scopes?.Where(scope => !string.IsNullOrWhiteSpace(scope)).Select(scope => scope.Trim()) ?? [],
            StringComparer.Ordinal);

    private bool IsMatchingBootedDevice(string suppliedUdid, string sessionUdid)
        => string.Equals(suppliedUdid, sessionUdid, StringComparison.OrdinalIgnoreCase)
            && IsBootedDevice(suppliedUdid);

    private async Task CloseWebRtcSessionAsync(string sessionId)
    {
        if (!webRtcSessions.TryRemove(sessionId, out var registration))
        {
            return;
        }

        registration.Session.InputReceived -= registration.InputHandler;
        try
        {
            await registration.InputDispatcher.DisposeAsync().ConfigureAwait(false);
            await registration.Session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (registration.CompanionDevice is not null)
            {
                CompanionConnectionsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void ApplyCompanionPresence(
        string sessionId,
        SessionRegistration registration,
        JsonElement payload)
    {
        var device = payload.Deserialize<CompanionPresenceMessage>(JsonOptions)?.Normalize();
        if (device is null)
        {
            return;
        }

        var current = registration;
        while (true)
        {
            var updated = current with
            {
                CompanionDevice = device,
                ConnectedAtUtc = current.ConnectedAtUtc ?? DateTimeOffset.UtcNow
            };
            if (webRtcSessions.TryUpdate(sessionId, updated, current))
            {
                if (current.CompanionDevice != device)
                {
                    CompanionConnectionsChanged?.Invoke(this, EventArgs.Empty);
                }
                return;
            }

            if (!webRtcSessions.TryGetValue(sessionId, out current))
            {
                return;
            }
        }
    }

    private bool TokenMatches(string suppliedToken)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(sessionToken);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
        return expectedBytes.Length == suppliedBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static string CreateSessionToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
