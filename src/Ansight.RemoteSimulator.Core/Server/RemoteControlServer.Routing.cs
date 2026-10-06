using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ansight.RemoteSimulator.Core.Agent;
using Ansight.RemoteSimulator.Core.Annotations;
using Ansight.RemoteSimulator.Core.Devices;
using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Server.WebRtc;
using Ansight.RemoteSimulator.Core.WebRtc;
using static Ansight.RemoteSimulator.Core.Server.RemoteControlHttpTransport;

namespace Ansight.RemoteSimulator.Core.Server;

public sealed partial class RemoteControlServer
{
    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            try
            {
                await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try
                {
                    await WriteJsonAsync(
                        client.GetStream(),
                        HttpStatusCode.InternalServerError,
                        new ErrorResponse(ex.Message),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception suppressedException)
                {
                    System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        var target = new Uri($"http://localhost{request.Target}");
        var query = ParseQuery(target.Query);
        if (!query.TryGetValue("token", out var suppliedToken) || !TokenMatches(suppliedToken))
        {
            await WriteTextAsync(
                stream,
                HttpStatusCode.Unauthorized,
                "The viewer URL is missing its session token.",
                "text/plain; charset=utf-8",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (accessAuthorizer is not null
            && (!allowUnauthenticatedLoopback || !IsLoopback(client))
            && !await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false))
        {
            await WriteJsonAsync(
                stream,
                HttpStatusCode.Unauthorized,
                new ErrorResponse("Sign in to an authorized Ansight account to access this developer machine."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (target.AbsolutePath is "/api/state" or "/api/location/set" or "/api/location/clear"
            or "/api/webrtc/offer" or "/api/frame" or "/api/input" or "/api/button"
            or "/api/key" or "/api/text")
        {
            await RefreshDevicesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (request.Method == "GET" && target.AbsolutePath == "/api/state")
        {
            var snapshot = runtimeSource.Current;
            var inventory = deviceLifecycleSource switch
            {
                IRemoteDeviceInventorySource inventorySource =>
                    await inventorySource.ListInventoryAsync(cancellationToken).ConfigureAwait(false),
                not null => new RemoteDeviceInventory(
                    await deviceLifecycleSource.ListBootableDevicesAsync(cancellationToken).ConfigureAwait(false),
                    await deviceLifecycleSource.ListInstalledApplicationsAsync(cancellationToken).ConfigureAwait(false)),
                _ => new RemoteDeviceInventory(
                    [], new Dictionary<string, IReadOnlyList<RemoteInstalledApplication>>())
            };
            IReadOnlyList<RemoteAnnotationSession> annotationSessions = annotationSource is null
                ? []
                : await annotationSource
                    .ListLiveSessionsAsync(cancellationToken)
                    .ConfigureAwait(false);
            var response = new RemoteStateResponse(
                snapshot.CapturedAtUtc,
                snapshot.Devices,
                inventory.BootableDevices,
                inventory.InstalledApplications,
                annotationSessions,
                snapshot.Error,
                inputSink.BackendName,
                inputSink.Status,
                webRtcSessionFactory?.BackendName ?? "adaptive-image-polling",
                webRtcSessionFactory?.Status ?? "WebRTC is unavailable; image polling is enabled.");
            await WriteJsonAsync(stream, HttpStatusCode.OK, response, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/device/start")
        {
            if (deviceLifecycleSource is null)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.ServiceUnavailable,
                    new ErrorResponse("Starting devices is unavailable on this host."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var startRequest = JsonSerializer.Deserialize<RemoteDeviceStartRequest>(request.Body, JsonOptions);
            if (string.IsNullOrWhiteSpace(startRequest?.Identifier))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    new ErrorResponse("A stopped device identifier is required."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await deviceLifecycleSource
                .StartDeviceAsync(startRequest.Identifier, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict,
                result.IsSuccess ? (object)result : new ErrorResponse(result.Message),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/location/set")
        {
            if (deviceLocationSource is null)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.ServiceUnavailable,
                    new ErrorResponse("Device location control is unavailable on this host."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var locationRequest = JsonSerializer.Deserialize<RemoteDeviceLocationRequest>(request.Body, JsonOptions);
            if (locationRequest is null
                || string.IsNullOrWhiteSpace(locationRequest.DeviceUdid)
                || !double.IsFinite(locationRequest.Latitude)
                || locationRequest.Latitude is < -90d or > 90d
                || !double.IsFinite(locationRequest.Longitude)
                || locationRequest.Longitude is < -180d or > 180d)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    new ErrorResponse("A booted device and valid latitude/longitude are required."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!IsBootedDevice(locationRequest.DeviceUdid))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.NotFound,
                    new ErrorResponse("The selected device is not booted."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await deviceLocationSource
                .SetLocationAsync(locationRequest, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict,
                result.IsSuccess ? (object)result : new ErrorResponse(result.Message),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/location/clear")
        {
            if (deviceLocationSource is null)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.ServiceUnavailable,
                    new ErrorResponse("Device location control is unavailable on this host."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var clearRequest = JsonSerializer.Deserialize<RemoteDeviceLocationClearRequest>(request.Body, JsonOptions);
            if (clearRequest is null || string.IsNullOrWhiteSpace(clearRequest.DeviceUdid))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    new ErrorResponse("A booted device is required."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!IsBootedDevice(clearRequest.DeviceUdid))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.NotFound,
                    new ErrorResponse("The selected device is not booted."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await deviceLocationSource
                .ClearLocationAsync(clearRequest, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict,
                result.IsSuccess ? (object)result : new ErrorResponse(result.Message),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/annotation")
        {
            if (annotationSource is null)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.ServiceUnavailable,
                    new ErrorResponse("Live-session annotations are unavailable on this host."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var annotationRequest = JsonSerializer.Deserialize<RemoteAnnotationRequest>(request.Body, JsonOptions);
            if (annotationRequest is null
                || string.IsNullOrWhiteSpace(annotationRequest.DeviceUdid)
                || string.IsNullOrWhiteSpace(annotationRequest.Comment))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    new ErrorResponse("A device and annotation comment are required."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await annotationSource
                .CreateAnnotationAsync(annotationRequest, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict,
                result.IsSuccess ? (object)result : new ErrorResponse(result.Message),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/annotation/update")
        {
            if (annotationSource is null)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.ServiceUnavailable,
                    new ErrorResponse("Live-session annotations are unavailable on this host."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var updateRequest = JsonSerializer.Deserialize<RemoteAnnotationUpdateRequest>(
                request.Body,
                JsonOptions);
            if (updateRequest is null
                || string.IsNullOrWhiteSpace(updateRequest.DeviceUdid)
                || string.IsNullOrWhiteSpace(updateRequest.AnnotationId)
                || string.IsNullOrWhiteSpace(updateRequest.Comment)
                || string.IsNullOrWhiteSpace(updateRequest.BatchId)
                || string.IsNullOrWhiteSpace(updateRequest.FrameId))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    new ErrorResponse("A complete annotation update is required."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await annotationSource
                .UpdateAnnotationAsync(updateRequest, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict,
                result.IsSuccess ? (object)result : new ErrorResponse(result.Message),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/annotation/delete")
        {
            if (annotationSource is null)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.ServiceUnavailable,
                    new ErrorResponse("Live-session annotations are unavailable on this host."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var deleteRequest = JsonSerializer.Deserialize<RemoteAnnotationDeleteRequest>(
                request.Body,
                JsonOptions);
            if (deleteRequest is null
                || string.IsNullOrWhiteSpace(deleteRequest.DeviceUdid)
                || string.IsNullOrWhiteSpace(deleteRequest.AnnotationId)
                || string.IsNullOrWhiteSpace(deleteRequest.BatchId)
                || string.IsNullOrWhiteSpace(deleteRequest.FrameId))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    new ErrorResponse("A complete annotation delete request is required."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await annotationSource
                .DeleteAnnotationAsync(deleteRequest, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict,
                result.IsSuccess ? (object)result : new ErrorResponse(result.Message),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/annotation/batch/start")
        {
            if (annotationSource is null)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.ServiceUnavailable,
                    new ErrorResponse("Live-session annotations are unavailable on this host."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var batchRequest = JsonSerializer.Deserialize<RemoteAnnotationBatchStartRequest>(
                request.Body,
                JsonOptions);
            if (batchRequest is null
                || string.IsNullOrWhiteSpace(batchRequest.DeviceUdid)
                || string.IsNullOrWhiteSpace(batchRequest.BatchId))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    new ErrorResponse("A device and annotation batch id are required."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await annotationSource
                .StartAnnotationBatchAsync(batchRequest, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict,
                result.IsSuccess ? (object)result : new ErrorResponse(result.Message),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/agent/chats")
        {
            if (agentChatSource is null)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.ServiceUnavailable,
                    new ErrorResponse("Agent chat discovery is unavailable on this host."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var chatRequest = JsonSerializer.Deserialize<RemoteAgentChatListRequest>(request.Body, JsonOptions);
            if (chatRequest is null || string.IsNullOrWhiteSpace(chatRequest.SessionId))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    new ErrorResponse("An Ansight session id is required to list agent chats."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await agentChatSource
                .ListAgentChatsAsync(chatRequest, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                result.IsSuccess ? (object)result : new ErrorResponse(result.Message),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/agent/tasks")
        {
            if (agentChatSource is null)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.ServiceUnavailable,
                    new ErrorResponse("Agent task history is unavailable on this host."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var taskRequest = JsonSerializer.Deserialize<RemoteAgentTaskListRequest>(request.Body, JsonOptions);
            if (taskRequest is null)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    new ErrorResponse("A valid agent task history request is required."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await agentChatSource
                .ListAgentTasksAsync(taskRequest, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                result.IsSuccess ? (object)result : new ErrorResponse(result.Message),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/annotation/batch/submit")
        {
            if (agentChatSource is null)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.ServiceUnavailable,
                    new ErrorResponse("Agent submission is unavailable on this host."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var submissionRequest = JsonSerializer.Deserialize<RemoteAnnotationBatchSubmissionRequest>(
                request.Body,
                JsonOptions);
            if (submissionRequest is null
                || string.IsNullOrWhiteSpace(submissionRequest.BatchId)
                || string.IsNullOrWhiteSpace(submissionRequest.SessionId)
                || string.IsNullOrWhiteSpace(submissionRequest.FrameId)
                || submissionRequest.AnnotationIds is null
                || submissionRequest.AnnotationIds.Count == 0
                || string.IsNullOrWhiteSpace(submissionRequest.Provider)
                || string.IsNullOrWhiteSpace(submissionRequest.ChatSessionId))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    new ErrorResponse("A complete annotation batch and destination chat are required."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await agentChatSource
                .SubmitAnnotationBatchAsync(submissionRequest, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                result.IsSuccess ? HttpStatusCode.Accepted : HttpStatusCode.Conflict,
                result.IsSuccess ? (object)result : new ErrorResponse(result.Message),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && target.AbsolutePath == "/api/recordings")
        {
            if (recordingSource is null)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.ServiceUnavailable,
                    new ErrorResponse("Recording review is unavailable on this host."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var recordings = await recordingSource.ListAsync(cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(stream, HttpStatusCode.OK, recordings, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && target.AbsolutePath == "/api/recording")
        {
            if (recordingSource is null || !query.TryGetValue("id", out var recordingId))
            {
                await WriteJsonAsync(
                    stream,
                    recordingSource is null ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest,
                    new ErrorResponse(recordingSource is null
                        ? "Recording review is unavailable on this host."
                        : "A recording id is required."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var recording = await recordingSource.GetAsync(recordingId, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                recording is null ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                recording ?? (object)new ErrorResponse("The recording was not found."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && target.AbsolutePath == "/api/recording/frame")
        {
            if (recordingSource is null
                || !query.TryGetValue("id", out var recordingId)
                || !query.TryGetValue("frame", out var frameId))
            {
                await WriteJsonAsync(
                    stream,
                    recordingSource is null ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest,
                    new ErrorResponse(recordingSource is null
                        ? "Recording review is unavailable on this host."
                        : "A recording id and frame id are required."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var frame = await recordingSource
                .GetFrameAsync(recordingId, frameId, cancellationToken)
                .ConfigureAwait(false);
            if (frame is null)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.NotFound,
                    new ErrorResponse("The recording frame was not found."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(
                stream,
                HttpStatusCode.OK,
                frame.ContentType,
                frame.Content,
                cancellationToken,
                "Cache-Control: private, max-age=300\r\n").ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && target.AbsolutePath == "/api/recording/icon")
        {
            if (recordingSource is null || !query.TryGetValue("id", out var recordingId))
            {
                await WriteJsonAsync(
                    stream,
                    recordingSource is null ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest,
                    new ErrorResponse(recordingSource is null
                        ? "Recording review is unavailable on this host."
                        : "A recording id is required."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var icon = await recordingSource
                .GetAppIconAsync(recordingId, cancellationToken)
                .ConfigureAwait(false);
            if (icon is null)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.NotFound,
                    new ErrorResponse("The recording app icon was not found."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(
                stream,
                HttpStatusCode.OK,
                icon.ContentType,
                icon.Content,
                cancellationToken,
                "Cache-Control: private, max-age=86400\r\n").ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && target.AbsolutePath == "/api/recording/evidence")
        {
            if (recordingSource is null
                || !query.TryGetValue("id", out var recordingId)
                || !query.TryGetValue("start", out var startText)
                || !query.TryGetValue("end", out var endText)
                || !DateTimeOffset.TryParse(startText, out var startUtc)
                || !DateTimeOffset.TryParse(endText, out var endUtc)
                || endUtc < startUtc)
            {
                await WriteJsonAsync(
                    stream,
                    recordingSource is null ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest,
                    new ErrorResponse(recordingSource is null
                        ? "Recording review is unavailable on this host."
                        : "A recording id and valid evidence time window are required."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var maximumLogCount = ReadBoundedQueryCount(query, "maxLogs", 250, 1, 1_000);
            var maximumMetricSampleCount = ReadBoundedQueryCount(query, "maxMetrics", 2_000, 1, 10_000);
            var evidence = await recordingSource
                .GetEvidenceAsync(
                    recordingId,
                    startUtc,
                    endUtc,
                    maximumLogCount,
                    maximumMetricSampleCount,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                evidence is null ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                evidence ?? (object)new ErrorResponse("The recording was not found."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/webrtc/offer")
        {
            var offerRequest = JsonSerializer.Deserialize<OfferRequest>(request.Body, JsonOptions)
                ?? throw new InvalidOperationException("A WebRTC offer request is required.");
            if (!string.Equals(offerRequest.Type, "offer", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(offerRequest.Sdp))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    new ErrorResponse("A valid browser WebRTC offer is required."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            if (webRtcSessionFactory is null || !webRtcSessionFactory.SupportsDevice(offerRequest.DeviceUdid))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.ServiceUnavailable,
                    new ErrorResponse(webRtcSessionFactory?.Status ?? "The WebRTC video backend is unavailable."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var framesPerSecond = offerRequest.FramesPerSecond ?? 30;
            if (framesPerSecond is not (30 or 60))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    new ErrorResponse("The WebRTC frame rate must be either 30 or 60 FPS."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!IsBootedDevice(offerRequest.DeviceUdid))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.NotFound,
                    new ErrorResponse("The selected device is not booted."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var browserOffer = new WebRtcSessionDescription(
                offerRequest.Type,
                TerminateSdp(offerRequest.Sdp));
            var response = await CreateWebRtcAnswerAsync(
                offerRequest.DeviceUdid,
                framesPerSecond,
                browserOffer,
                NormalizeScopes(offerRequest.GrantedScopes),
                cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(stream, HttpStatusCode.OK, response, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/webrtc/close")
        {
            if (query.TryGetValue("session", out var sessionId))
            {
                await CloseWebRtcSessionAsync(sessionId).ConfigureAwait(false);
            }
            await WriteJsonAsync(
                stream,
                HttpStatusCode.OK,
                new AnswerResponse(sessionId ?? string.Empty, "closed"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && target.AbsolutePath == "/api/frame")
        {
            if (!query.TryGetValue("udid", out var deviceUdid) || !IsBootedDevice(deviceUdid))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.NotFound,
                    new ErrorResponse("The selected device is not booted."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var frame = await frameSource.CaptureAsync(deviceUdid, cancellationToken).ConfigureAwait(false);
            await WriteResponseAsync(
                stream,
                HttpStatusCode.OK,
                frame.ContentType,
                frame.Content,
                cancellationToken,
                "Cache-Control: no-store, no-cache, must-revalidate\r\n").ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/input")
        {
            var pointerEvent = JsonSerializer.Deserialize<RemotePointerEvent>(request.Body, JsonOptions)
                ?? throw new InvalidOperationException("Pointer payload is required.");
            pointerEvent = pointerEvent.NormalizeAndValidate();
            if (!IsBootedDevice(pointerEvent.DeviceUdid))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.NotFound,
                    new ErrorResponse("The selected device is not booted."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await inputSink.SendAsync(pointerEvent, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                result,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/button")
        {
            var buttonEvent = JsonSerializer.Deserialize<RemoteButtonEvent>(request.Body, JsonOptions)
                ?? throw new InvalidOperationException("Button payload is required.");
            buttonEvent = buttonEvent.NormalizeAndValidate();
            if (!IsBootedDevice(buttonEvent.DeviceUdid))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.NotFound,
                    new ErrorResponse("The selected device is not booted."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await inputSink.SendButtonAsync(buttonEvent, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                result,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/key")
        {
            var keyEvent = JsonSerializer.Deserialize<RemoteKeyEvent>(request.Body, JsonOptions)
                ?? throw new InvalidOperationException("Keyboard payload is required.");
            keyEvent = keyEvent.NormalizeAndValidate();
            if (!IsBootedDevice(keyEvent.DeviceUdid))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.NotFound,
                    new ErrorResponse("The selected device is not booted."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await inputSink.SendKeyAsync(keyEvent, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                result,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && target.AbsolutePath == "/api/text")
        {
            var textEvent = JsonSerializer.Deserialize<RemoteTextEvent>(request.Body, JsonOptions)
                ?? throw new InvalidOperationException("Text payload is required.");
            textEvent = textEvent.NormalizeAndValidate();
            if (!IsBootedDevice(textEvent.DeviceUdid))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.NotFound,
                    new ErrorResponse("The selected device is not booted."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await inputSink.SendTextAsync(textEvent, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(
                stream,
                result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                result,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
            stream,
            HttpStatusCode.NotFound,
            new ErrorResponse("Endpoint not found."),
            cancellationToken).ConfigureAwait(false);
    }
}
