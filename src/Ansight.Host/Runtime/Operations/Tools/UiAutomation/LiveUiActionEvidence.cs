using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class LiveUiActionEvidence
{
    public static async Task<ActionEvidenceCapture> CaptureAsync(
        IRuntimeState runtimeState,
        IApplicationPaths applicationPaths,
        IAppToolBridge appToolBridge,
        LiveUiTreeCapture tree,
        string actionId,
        string capability,
        string phase,
        string? correlationId,
        CancellationToken cancellationToken,
        bool captureScreenshot = true,
        IExternalSessionScreenshotCaptureManager? externalScreenshots = null)
    {
        var screenshot = !captureScreenshot
                         || RunRequestContext.IsTestRunCorrelationId(correlationId)
                            && string.Equals(phase, "before", StringComparison.Ordinal)
            ? null
            : await CaptureScreenshotAsync(
                runtimeState,
                applicationPaths,
                appToolBridge,
                tree.Session,
                actionId,
                phase,
                correlationId,
                cancellationToken, externalScreenshots: externalScreenshots);
        var persistedTree = SessionVisualTreePersistence.Persist(
            runtimeState,
            tree.Session,
            tree.ToolId,
            tree.RawPayload,
            new ActionEvidenceMetadata(actionId, capability, phase),
            screenshot);
        return new ActionEvidenceCapture(
            phase,
            persistedTree.Snapshot?.SnapshotId,
            persistedTree.Snapshot?.TreeHash,
            screenshot?.Frame.FrameId,
            screenshot?.Hash,
            persistedTree.IsSuccess,
            persistedTree.Message,
            screenshot?.ArtifactPath,
            screenshot?.Frame.Format);
    }

    public static JsonObject ToJson(ActionEvidenceCapture? capture)
    {
        return capture is null
            ? new JsonObject
            {
                ["persisted"] = false,
                ["message"] = "Evidence capture was unavailable."
            }
            : new JsonObject
            {
                ["phase"] = capture.Phase,
                ["persisted"] = capture.Persisted,
                ["visualTreeSnapshotId"] = capture.VisualTreeSnapshotId,
                ["treeHash"] = capture.TreeHash,
                ["screenshotFrameId"] = capture.ScreenshotFrameId,
                ["screenshotHash"] = capture.ScreenshotHash,
                ["message"] = capture.Message
            };
    }

    internal static async Task<PersistedScreenshotEvidence?> CaptureScreenshotAsync(
        IRuntimeState runtimeState,
        IApplicationPaths applicationPaths,
        IAppToolBridge appToolBridge,
        AppSessionSnapshot session,
        string actionId,
        string phase,
        string? correlationId,
        CancellationToken cancellationToken,
        bool afterScreenUpdates = false,
        IExternalSessionScreenshotCaptureManager? externalScreenshots = null)
    {
        if (session.CaptureSource == WorkspaceExecutionModes.Device)
        {
            var screenshot = await ReadExternalScreenshotAsync(externalScreenshots, session, 1024, cancellationToken).ConfigureAwait(false);
            return screenshot is null ? null
                : await PersistAppScreenshotAsync(runtimeState, session, screenshot, applicationPaths).ConfigureAwait(false);
        }
        if (RunRequestContext.IsTestRunCorrelationId(correlationId)
            && UsesHostManagedTestCapture(session))
        {
            var managedScreenshot = await ResolveHostManagedScreenshotAsync(
                runtimeState,
                applicationPaths,
                session,
                waitForFreshFrame: string.Equals(phase, "after", StringComparison.Ordinal),
                cancellationToken);
            if (managedScreenshot is not null)
            {
                return managedScreenshot;
            }
        }

        return await CaptureAppManagedScreenshotAsync(
            runtimeState,
            appToolBridge,
            session,
            actionId,
            phase,
            correlationId,
            cancellationToken,
            afterScreenUpdates);
    }

    private static bool UsesHostManagedTestCapture(AppSessionSnapshot session)
    {
        var device = session.DeviceProfile?.Device;
        return device is null
               || device.IsVirtual == true
               || device.IsEmulator == true
               || string.Equals(device.OsName, "android", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<PersistedScreenshotEvidence?> CaptureAppManagedScreenshotAsync(
        IRuntimeState runtimeState,
        IAppToolBridge appToolBridge,
        AppSessionSnapshot session,
        string actionId,
        string phase,
        string? correlationId,
        CancellationToken cancellationToken,
        bool afterScreenUpdates)
    {
        var screenshot = await ReadAppScreenshotAsync(appToolBridge, session, actionId, phase,
            correlationId, cancellationToken, afterScreenUpdates, 1_024).ConfigureAwait(false);
        return screenshot is null ? null
            : await PersistAppScreenshotAsync(runtimeState, session, screenshot).ConfigureAwait(false);
    }

    internal static async Task<AppScreenshot?> ReadExternalScreenshotAsync(
        IExternalSessionScreenshotCaptureManager? screenshots, AppSessionSnapshot session,
        int maxWidth, CancellationToken cancellationToken)
    {
        if (screenshots is null) return null;
        var bytes = await screenshots.CaptureFrameAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        var capturedAtUtc = DateTimeOffset.UtcNow;
        if (bytes is null) return null;
        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap is null) return null;
        var width = Math.Min(bitmap.Width, maxWidth);
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * (double)width / bitmap.Width));
        using var resized = bitmap.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
        if (resized is null) return null;
        using var encoded = resized.Encode(SKEncodedImageFormat.Png, 100);
        return new AppScreenshot(string.Empty, encoded.ToArray(), capturedAtUtc, "png", width, height);
    }

    // Probes are app-owned screenshot artifacts, but only the selected final frame
    // is added to the session evidence store. Do not turn settling into a recording.
    internal static async Task<AppScreenshot?> ReadAppScreenshotAsync(
        IAppToolBridge appToolBridge,
        AppSessionSnapshot session,
        string actionId,
        string phase,
        string? correlationId,
        CancellationToken cancellationToken,
        bool afterScreenUpdates,
        int maxWidth)
    {
        var response = await appToolBridge.CallToolWithCatalogRecoveryAsync(
            session.SessionId,
            RemoteAppToolIds.UiGetScreenshot,
            new JsonObject
            {
                ["format"] = "png",
                ["maxWidth"] = maxWidth,
                ["afterScreenUpdates"] = afterScreenUpdates
            },
            after: null,
            cancellationToken,
            new AppToolBridgeRequestContext("host-ui", $"{actionId}:{phase}", correlationId));
        if (!response.Success
            || response.Envelope?.Payload is not JsonObject payload
            || payload["result"] is not JsonObject result)
        {
            return null;
        }

        var artifactPath = LiveUiNodeQuery.ReadString(result, "artifactPath");
        if (artifactPath is null || !File.Exists(artifactPath))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(artifactPath, cancellationToken);
        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap is null)
        {
            return null;
        }

        var capturedAtUtc = DateTimeOffset.TryParse(
            LiveUiNodeQuery.ReadString(result, "capturedAtUtc"),
            out var parsedCapturedAtUtc)
            ? parsedCapturedAtUtc.ToUniversalTime()
            : DateTimeOffset.UtcNow;
        var format = LiveUiNodeQuery.ReadString(result, "format") ?? "png";
        return new AppScreenshot(artifactPath, bytes, capturedAtUtc, format, bitmap.Width, bitmap.Height);
    }

    internal static async Task<PersistedScreenshotEvidence?> PersistAppScreenshotAsync(
        IRuntimeState runtimeState, AppSessionSnapshot session, AppScreenshot screenshot,
        IApplicationPaths? applicationPaths = null)
    {
        var frame = await runtimeState.AddSessionEvidenceImageAsync(
            session.SessionId,
            screenshot.CapturedAtUtc,
            screenshot.Format,
            screenshot.Width,
            screenshot.Height,
            quality: 100,
            screenshot.Bytes);
        return frame is null
            ? null
            : new PersistedScreenshotEvidence(
                frame,
                Convert.ToHexString(SHA256.HashData(screenshot.Bytes)).ToLowerInvariant(),
                string.IsNullOrEmpty(screenshot.ArtifactPath) && applicationPaths is not null
                    ? SessionFileLocator.ResolveScreenshotPath(applicationPaths, session, frame)
                    : screenshot.ArtifactPath);
    }

    private static async Task<PersistedScreenshotEvidence?> ResolveHostManagedScreenshotAsync(
        IRuntimeState runtimeState,
        IApplicationPaths applicationPaths,
        AppSessionSnapshot session,
        bool waitForFreshFrame,
        CancellationToken cancellationToken)
    {
        var minimumCapturedAtUtc = waitForFreshFrame ? DateTimeOffset.UtcNow : DateTimeOffset.MinValue;
        var deadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(
            session.CaptureSource == WorkspaceExecutionModes.Device ? 2500 : 650);
        AppSessionSnapshot? currentSession = null;
        SessionImageFrame? frame = null;
        do
        {
            if (runtimeState.TryGetSessionSnapshot(session.SessionId, out currentSession)
                && currentSession is not null)
            {
                frame = currentSession.Images
                    .OrderBy(candidate => candidate.CapturedAtUtc)
                    .LastOrDefault();
                if (frame is not null
                    && (!waitForFreshFrame || frame.CapturedAtUtc >= minimumCapturedAtUtc))
                {
                    break;
                }
            }

            if (!waitForFreshFrame || DateTimeOffset.UtcNow >= deadlineUtc)
            {
                break;
            }

            await Task.Delay(50, cancellationToken);
        } while (true);

        if (currentSession is null || frame is null)
        {
            return null;
        }

        if (waitForFreshFrame && frame.CapturedAtUtc < minimumCapturedAtUtc)
        {
            return null;
        }

        var artifactPath = SessionFileLocator.ResolveScreenshotPath(
            applicationPaths,
            currentSession,
            frame);
        if (!File.Exists(artifactPath))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(artifactPath, cancellationToken);
        return new PersistedScreenshotEvidence(
            frame,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            artifactPath);
    }
}

internal sealed record AppScreenshot(
    string ArtifactPath, byte[] Bytes, DateTimeOffset CapturedAtUtc, string Format, int Width, int Height);

internal static class LiveUiEvidenceCapture
{
    public static async Task<AfterActionCapture> CaptureAfterAsync(
        IRuntimeState runtimeState,
        IApplicationPaths applicationPaths,
        IAppToolBridge appToolBridge,
        UiInputRouter uiInputRouter,
        LiveUiTreeCapture beforeCapture,
        string actionId,
        string capability,
        string toolName,
        string? correlationId,
        int settleMilliseconds = 200,
        CancellationToken cancellationToken = default,
        bool captureScreenshot = true,
        IExternalSessionScreenshotCaptureManager? externalScreenshots = null)
    {
        if (settleMilliseconds > 0)
        {
            await Task.Delay(settleMilliseconds, cancellationToken);
        }

        var captureResult = await LiveUiTreeCapture.CaptureAsync(
            beforeCapture.Session,
            appToolBridge,
            uiInputRouter,
            toolName,
            correlationId,
            cancellationToken);
        if (!captureResult.IsSuccess || captureResult.Capture is null)
        {
            return new AfterActionCapture(
                new ActionEvidenceCapture(
                    "after",
                    VisualTreeSnapshotId: null,
                    TreeHash: null,
                    ScreenshotFrameId: null,
                    ScreenshotHash: null,
                    Persisted: false,
                    captureResult.Message),
                Capture: null);
        }

        var evidence = await LiveUiActionEvidence.CaptureAsync(
            runtimeState,
            applicationPaths,
            appToolBridge,
            captureResult.Capture,
            actionId,
            capability,
            "after",
            correlationId,
            cancellationToken,
            captureScreenshot, externalScreenshots);
        return new AfterActionCapture(evidence, captureResult.Capture);
    }
}

internal sealed record ActionEvidenceCapture(
    string Phase,
    string? VisualTreeSnapshotId,
    string? TreeHash,
    string? ScreenshotFrameId,
    string? ScreenshotHash,
    bool Persisted,
    string Message,
    string? ScreenshotArtifactPath = null,
    string? ScreenshotFormat = null);

internal sealed record AfterActionCapture(
    ActionEvidenceCapture Evidence,
    LiveUiTreeCapture? Capture);
