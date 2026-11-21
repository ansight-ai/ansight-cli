using System.Text.Json;

namespace Ansight.Cli.LocalSimulator;

internal sealed partial class CliRemoteAgentChatSource
{
    private async Task RefreshDeliveryStatusesAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (!await mutationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var receipts = store.ReadReceipts()
                .Where(receipt => receipt.Link.Provider == "codex" && (sessionId is null || receipt.Link.SessionId == sessionId))
                .ToArray();

            for (var index = 0; index < receipts.Length; index++)
            {
                var receipt = receipts[index];

                if (receipt.Link.Status is "creating" or "dispatching")
                {
                    // Holding the submission gate means these stages were left
                    // behind by a stopped host, rather than a current delivery.
                    receipts[index] = SaveReceipt(receipt with
                    {
                        Link = receipt.Link with
                        {
                            Status = "needs_attention",
                            StatusMessage = "Delivery was interrupted. Check Codex before sending another annotation set.",
                            UpdatedAtUtc = DateTimeOffset.UtcNow
                        }
                    });
                }
            }

            var pending = receipts.Where(receipt =>
            {
                var needsStatus = receipt.Link.Status is "accepted" or "steered" or "queued" or "running" or "needs_attention";

                return needsStatus && !string.IsNullOrEmpty(receipt.Link.ProviderTaskId);
            }).OrderByDescending(static receipt => receipt.Link.CreatedAtUtc).Take(20).ToArray();

            if (pending.Length == 0)
            {
                return;
            }

            await using var connection = await connections.ConnectAsync(cancellationToken).ConfigureAwait(false);

            foreach (var receipt in pending)
            {
                try
                {
                    var response = await connection.CallAsync("thread/turns/list", new
                    {
                        threadId = receipt.Link.ProviderTaskId,
                        limit = 20,
                        sortDirection = "desc",
                        itemsView = receipt.Link.ProviderTurnId is null ? "full" : "notLoaded"
                    }, cancellationToken).ConfigureAwait(false);
                    var turn = response.GetProperty("data").EnumerateArray()
                        .FirstOrDefault(candidate => MatchesDelivery(candidate, receipt));

                    if (turn.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var status = ReadString(turn, "status") switch
                    {
                        "completed" => "ready_for_review",
                        "failed" => "work_failed",
                        "interrupted" => "interrupted",
                        "inProgress" => "running",
                        _ => receipt.Link.Status
                    };

                    if (status == receipt.Link.Status)
                    {
                        continue;
                    }

                    var message = status switch
                    {
                        "ready_for_review" => "Codex finished this turn. Review the result in the app.",
                        "work_failed" => "Codex could not finish this turn. Open the task for details.",
                        "interrupted" => "This Codex turn was interrupted.",
                        "running" => "Codex is working on these annotations.",
                        _ => receipt.Link.StatusMessage
                    };
                    SaveReceipt(receipt with
                    {
                        Link = receipt.Link with
                        {
                            ProviderTurnId = ReadString(turn, "id"),
                            Status = status,
                            StatusMessage = message,
                            UpdatedAtUtc = DateTimeOffset.UtcNow
                        }
                    });
                }
                catch (CodexRpcException)
                {
                    // A task may have been removed or moved while history was
                    // open. Keep its receipt and continue with the other tasks.
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Saved delivery history remains available while Codex is offline.
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private static bool MatchesDelivery(JsonElement turn, CompanionAgentReceipt receipt)
    {
        if (receipt.Link.ProviderTurnId is not null)
        {
            return ReadString(turn, "id") == receipt.Link.ProviderTurnId;
        }

        if (!turn.TryGetProperty("items", out var items))
        {
            return false;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (ReadString(item, "type") != "userMessage" || !item.TryGetProperty("content", out var content))
            {
                continue;
            }

            if (content.EnumerateArray().Any(input => ReadString(input, "text") == receipt.Link.SubmittedPrompt))
            {
                return true;
            }
        }

        return false;
    }
}
