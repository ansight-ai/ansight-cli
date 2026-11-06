using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Files;

internal sealed class ExternalSandboxCopy : IDisposable
{
    private readonly string directory;

    public ExternalSandboxCopy(string directory, string path)
    {
        this.directory = directory;
        this.Path = path;
    }

    public string Path { get; }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
