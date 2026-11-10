using System.Security.Cryptography;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private sealed record LocalRunnerOpenAiRequest(string? ApiKey, bool Remove = false);

    private async Task<bool> ReadRunnerOpenAiAsync(HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        await WriteJsonAsync(response, new { isConfigured = runtime.OpenAiCredentials.IsConfigured },
            HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> UpdateRunnerOpenAiAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await ReadJsonAsync<LocalRunnerOpenAiRequest>(request, cancellationToken).ConfigureAwait(false);

            if (body.Remove)
            {
                runtime.OpenAiCredentials.Remove();
            }
            else
            {
                runtime.OpenAiCredentials.Save(body.ApiKey);
            }

            await WriteJsonAsync(response, new
            {
                isSuccess = true,
                isConfigured = runtime.OpenAiCredentials.IsConfigured,
                message = body.Remove
                    ? "OpenAI API key removed. New runs will use Ansight AI."
                    : "OpenAI API key saved. New AI runs on this machine will use it."
            }, HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            await WriteJsonAsync(response, new
            {
                isSuccess = false,
                message = "Enter a valid OpenAI API key without spaces or line breaks."
            }, HttpStatusCode.BadRequest, false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        {
            await WriteJsonAsync(response, new
            {
                isSuccess = false,
                message = "Unable to update the OpenAI API key in secure storage."
            }, HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}
