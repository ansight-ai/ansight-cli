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
    private async Task<bool> TryHandleDiagnosticsPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/doctor" when isExplorer:
                {
                    var body = await ReadJsonAsync<LocalDoctorRequest>(request, cancellationToken);
                    var report = await runtime.SystemReports.CollectAsync(body.IncludeSecretMetadata, cancellationToken);
                    await WriteJsonAsync(response, report, HttpStatusCode.OK, false, cancellationToken);
                    return true;
                }
        }

        return false;
    }

    private IReadOnlyList<LocalHostLogFile> ListHostLogs()
    {
        var logsPath = runtime.ApplicationPaths.ApplicationLogsPath;
        if (!Directory.Exists(logsPath))
        {
            return [];
        }

        return Directory.EnumerateFiles(logsPath, "*", SearchOption.TopDirectoryOnly).Select(static path => new FileInfo(path)).OrderByDescending(static file => file.LastWriteTimeUtc).Take(100).Select(static file => new LocalHostLogFile(file.Name, file.Length, file.LastWriteTimeUtc)).ToArray();
    }

    private async Task WriteHostLogAsync(HttpListenerResponse response, string? fileName, bool isHead, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Choose a valid host log file.");
        }

        var path = Path.Combine(runtime.ApplicationPaths.ApplicationLogsPath, fileName);
        if (!File.Exists(path))
        {
            await WriteTextAsync(response, "Host log file not found.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }

        var file = new FileInfo(path);
        var offset = Math.Max(0, file.Length - MaximumHostLogReadBytes);
        var length = checked((int)(file.Length - offset));
        var bytes = new byte[length];
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true))
        {
            stream.Position = offset;
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        await WriteBytesAsync(response, bytes, "text/plain; charset=utf-8", HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
    }
}
