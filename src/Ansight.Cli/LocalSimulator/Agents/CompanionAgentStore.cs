using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ansight.RemoteSimulator.Core.Agent;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CompanionAgentStore
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string directory;

    public CompanionAgentStore(string baseFolderPath)
    {
        directory = Path.Combine(baseFolderPath, "companion-agents");
    }

    public RemoteAgentProjectLink? ReadProject(string appId)
        => Read<RemoteAgentProjectLink>("projects", appId);

    public void SaveProject(string appId, RemoteAgentProjectLink? project)
    {
        var path = GetPath("projects", appId);

        if (project is null)
        {
            File.Delete(path);
            return;
        }

        Write(path, project);
    }

    public CompanionAgentReceipt? ReadReceipt(string sessionId, string batchId)
        => Read<CompanionAgentReceipt>("receipts", sessionId + "\n" + batchId);

    public void SaveReceipt(CompanionAgentReceipt receipt)
        => Write(GetPath("receipts", receipt.Link.SessionId + "\n" + receipt.Link.AnnotationBatchId), receipt);

    public IReadOnlyList<CompanionAgentReceipt> ReadReceipts()
    {
        var receiptsDirectory = Path.Combine(directory, "receipts");

        if (!Directory.Exists(receiptsDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(receiptsDirectory, "*.json")
            .Select(path => JsonSerializer.Deserialize<CompanionAgentReceipt>(File.ReadAllText(path), jsonOptions)
                ?? throw new InvalidDataException("A saved Codex delivery receipt could not be read."))
            .ToArray();
    }

    private T? Read<T>(string category, string key)
    {
        var path = GetPath(category, key);

        if (!File.Exists(path))
        {
            return default;
        }

        // Corrupt settings or receipts must not silently enable a different
        // destination or a duplicate delivery.
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), jsonOptions)
            ?? throw new InvalidDataException("The saved Codex connection state could not be read.");
    }

    private string GetPath(string category, string key)
    {
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

        return Path.Combine(directory, category, name + ".json");
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, jsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
