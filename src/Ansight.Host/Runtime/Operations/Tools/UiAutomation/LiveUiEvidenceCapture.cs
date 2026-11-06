using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

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
        IExternalSessionScreenshotCaptureManager? externalScreenshots = null,
        LiveUiSelector? scrollSelector = null)
    {
        if (settleMilliseconds > 0)
        {
            await Task.Delay(settleMilliseconds, cancellationToken);
        }

        var captureResult = scrollSelector is null
            ? await LiveUiTreeCapture.CaptureAsync(
                beforeCapture.Session,
                appToolBridge,
                uiInputRouter,
                toolName,
                correlationId,
                cancellationToken)
            : await LiveUiTreeCapture.CaptureForSelectorAsync(
                beforeCapture.Session, appToolBridge, uiInputRouter, toolName, correlationId,
                scrollSelector, cancellationToken, allowCached: false, targetSource: beforeCapture.ToolId);
        var scrollSettled = true;

        if (scrollSelector is not null && captureResult.IsSuccess)
        {
            var settled = await LiveUiScrollSettlement.CaptureAsync(captureResult, scrollSelector,
                token => LiveUiTreeCapture.CaptureForSelectorAsync(
                    beforeCapture.Session, appToolBridge, uiInputRouter, toolName, correlationId,
                    scrollSelector, token, allowCached: false, targetSource: beforeCapture.ToolId),
                cancellationToken).ConfigureAwait(false);
            captureResult = settled.Result;
            scrollSettled = settled.IsSettled;
        }

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

        return new AfterActionCapture(evidence, captureResult.Capture) { ScrollSettled = scrollSettled };
    }
}
