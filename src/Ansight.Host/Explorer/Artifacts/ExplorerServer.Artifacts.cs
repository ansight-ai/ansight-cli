using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ansight.Host.AppGraphs;
using Ansight.Host.Files;
using Ansight.Host.Trends;
using Ansight.Host.Runtime.BinaryTransfers;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Infrastructure.Preferences;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer : IAsyncDisposable
{
    private static async Task WriteTraceEvidenceFileAsync(HttpListenerResponse response, string path, bool isHead, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path);
        var contentType = string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) ? "application/json; charset=utf-8" : ResolveImageContentType(extension);
        var file = new FileInfo(path);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = contentType;
        response.ContentLength64 = file.Length;
        if (!isHead)
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024, useAsync: true);
            await stream.CopyToAsync(response.OutputStream, cancellationToken).ConfigureAwait(false);
        }

        response.Close();
    }

    private static async Task WriteDownloadFileAsync(HttpListenerResponse response, string path, string fileName, string contentType, bool isHead, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = contentType;
        response.ContentLength64 = file.Length;
        response.Headers["Content-Disposition"] = $"attachment; filename=\"{FileNameUtil.Sanitize(fileName)}\"";
        if (!isHead)
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
            await stream.CopyToAsync(response.OutputStream, cancellationToken).ConfigureAwait(false);
        }

        response.Close();
    }

    private async Task WriteLiveFileContentAsync(HttpListenerRequest request, HttpListenerResponse response, string sessionId, string? sandboxRoot, string sandboxPath, bool isHead, CancellationToken cancellationToken)
    {
        long offset = 0;
        long? remainingBytes = null;
        string? expectedVersion = null;
        var headersWritten = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = new JsonObject
            {
                ["path"] = sandboxPath,
                ["offsetBytes"] = offset,
                ["maxBytes"] = !headersWritten ? 1 : (int)Math.Min(LiveFileDownloadChunkBytes, remainingBytes ?? LiveFileDownloadChunkBytes),
                ["encoding"] = "base64"
            };
            if (!string.IsNullOrWhiteSpace(sandboxRoot))
            {
                arguments["root"] = sandboxRoot.Trim();
            }

            if (!string.IsNullOrWhiteSpace(expectedVersion))
            {
                arguments["expectedVersion"] = expectedVersion;
            }

            var toolResponse = await runtime.LiveFiles.CallAsync(sessionId, "files.download_file", arguments, cancellationToken).ConfigureAwait(false);
            if (!TryReadAppToolResult(toolResponse, out var result, out var errorMessage))
            {
                if (!headersWritten)
                {
                    await WriteTextAsync(response, errorMessage, HttpStatusCode.Conflict, isHead, cancellationToken).ConfigureAwait(false);
                    return;
                }

                throw new InvalidDataException(errorMessage);
            }

            byte[] bytes;
            try
            {
                var base64 = ReadOptionalString(result, "base64");
                bytes = string.IsNullOrWhiteSpace(base64) ? [] : Convert.FromBase64String(base64);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("The app returned an invalid base64 file chunk.", exception);
            }

            expectedVersion ??= ReadOptionalString(result, "version");
            if (!headersWritten)
            {
                headersWritten = true;
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = FileVisualizationService.ResolveMimeType(
                    Path.GetExtension(sandboxPath).ToLowerInvariant(),
                    ReadOptionalString(result, "mimeType") ?? ReadOptionalString(result, "contentType"));
                var sizeBytes = ReadOptionalInt64(result, "sizeBytes", -1);
                if (sizeBytes >= 0)
                {
                    var range = HttpFileRange.Resolve(GetFileRangeHeader(request, isHead), sizeBytes);
                    range.Apply(response, sizeBytes);
                    remainingBytes = range.Length;
                    if (isHead || range.Length == 0)
                    {
                        response.Close();
                        return;
                    }

                    if (range.Offset > 0)
                    {
                        offset = range.Offset;
                        continue;
                    }
                }
                else
                {
                    response.SendChunked = true;
                }

                if (isHead)
                {
                    response.Close();
                    return;
                }
            }

            var bytesToWrite = (int)Math.Min(bytes.Length, remainingBytes ?? bytes.Length);
            if (bytesToWrite > 0)
            {
                await response.OutputStream.WriteAsync(bytes.AsMemory(0, bytesToWrite), cancellationToken).ConfigureAwait(false);
                remainingBytes -= bytesToWrite;
            }

            if (remainingBytes == 0 || !ReadOptionalBoolean(result, "hasMore"))
            {
                if (remainingBytes > 0)
                {
                    throw new EndOfStreamException("The app returned an incomplete file download.");
                }
                response.Close();
                return;
            }

            var nextOffset = ReadOptionalInt64(result, "nextOffsetBytes", offset + bytes.Length);
            if (nextOffset <= offset)
            {
                throw new InvalidDataException("The app returned a non-advancing file download offset.");
            }

            offset = nextOffset;
        }
    }

    private async Task WriteArtifactContentAsync(HttpListenerRequest request, HttpListenerResponse response, string sessionId, string? snapshotId, string artifactPath, bool isHead, CancellationToken cancellationToken)
    {
        var source = await runtime.FileVisualizations.ResolveSessionArtifactContentAsync(sessionId, snapshotId, artifactPath, cancellationToken).ConfigureAwait(false);
        if (!source.IsSuccess || source.FilePath is null)
        {
            await WriteTextAsync(response, source.Message, HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }

        response.ContentType = source.MimeType;
        await using var stream = new FileStream(source.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var range = HttpFileRange.Resolve(GetFileRangeHeader(request, isHead), stream.Length);
        range.Apply(response, stream.Length);
        if (!isHead && range.Length > 0)
        {
            stream.Seek(range.Offset, SeekOrigin.Begin);
            var buffer = new byte[128 * 1024];
            var remainingBytes = range.Length;
            while (remainingBytes > 0)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remainingBytes)), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException("The artifact ended before its requested range was read.");
                }

                await response.OutputStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                remainingBytes -= bytesRead;
            }
        }

        response.Close();
    }

    private static string? GetFileRangeHeader(HttpListenerRequest request, bool isHead)
        // HEAD ignores Range. Without a validator, an If-Range request must receive the complete file.
        => isHead || !string.IsNullOrWhiteSpace(request.Headers["If-Range"])
            ? null
            : request.Headers["Range"];
}
