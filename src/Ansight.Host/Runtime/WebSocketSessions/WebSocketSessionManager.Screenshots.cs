using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.WebSocketSessions;

internal sealed partial class WebSocketSessionManager
{
    private async Task UpdatePeriodicScreenshotPolicyAsync(string sessionId)
    {
        var connection = connectionRegistry.Get(sessionId);

        if (connection is null || externalSessionScreenshotCaptureManager is null)
        {
            return;
        }

        await connection.PeriodicScreenshotPolicyLock.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!runtimeState.TryGetSessionSnapshot(sessionId, out var session)
                || string.IsNullOrWhiteSpace(session?.DeviceProfileJson))
            {
                return;
            }

            var profile = JsonNode.Parse(session.DeviceProfileJson);

            if (profile?[ExternalSessionScreenshotCapturePolicy.PeriodicControlVersionPropertyName]?.GetValue<int>() is not >= 1)
            {
                return; // Older SDKs retain their existing capture policy.
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await connection.SendRequestAsync(new ToolProtocolEnvelope
            {
                Type = ExternalSessionScreenshotCapturePolicy.PeriodicControlType,
                Id = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                Payload = new JsonObject
                {
                    ["paused"] = externalSessionScreenshotCaptureManager.IsPeriodicCaptureSuspended(sessionId)
                }
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Disconnects reset SDK capture state; never fail the execution for a policy update.
            runtimeState.AddSessionLog(sessionId, new LogEntry(DateTimeOffset.UtcNow,
                $"Could not update periodic screenshot capture: {exception.Message}")
            {
                Priority = LogPriority.Warning,
                Source = "Capture"
            });
        }
        finally
        {
            connection.PeriodicScreenshotPolicyLock.Release();
        }
    }
}
