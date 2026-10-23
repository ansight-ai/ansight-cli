using System.Text.Json.Nodes;

namespace Ansight.Host;

internal static class PenInputFile
{
    internal const int MaximumBytes = 1_048_576;

    internal static async Task<JsonObject> ReadAsync(string file, CancellationToken cancellationToken)
    {
        var path = WorkspaceFixturePath.Resolve(file, PenInputScope.CurrentRepositoryRoot);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 2 or > MaximumBytes)
            throw new ArgumentException("Pen files must be nonempty JSON no larger than 1 MiB.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            throw new ArgumentException("The pen file changed while it was read.");
        return Parse(bytes);
    }

    internal static JsonObject Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Pen files must be at most 1 MiB.");
        var document = JsonNode.Parse(bytes) as JsonObject
            ?? throw new ArgumentException("A pen file must contain an object.");
        if (document["schema"]?.GetValue<string>() != "ansight.pen-input/v1")
            throw new ArgumentException("Expected pen file schema ansight.pen-input/v1.");
        if (document.Any(property => property.Key is not ("schema" or "samples" or "description")))
            throw new ArgumentException("Unknown pen file property.");
        return document;
    }
}
