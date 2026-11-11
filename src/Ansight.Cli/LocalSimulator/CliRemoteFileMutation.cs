using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Cli.LocalSimulator;

internal sealed record CliRemoteFileMutation(string ToolId, JsonObject Arguments, JsonObject VerificationArguments)
{
    internal static readonly IReadOnlyDictionary<string, string> operations = new Dictionary<string, string>
    {
        ["files.write"] = "files.push_file",
        ["files.rename"] = "files.move_file",
        ["files.delete"] = "files.delete_file"
    };

    internal static CliRemoteFileMutation Create(string operation, JsonObject arguments)
    {
        if (!operations.TryGetValue(operation, out var toolId))
        {
            throw new ArgumentException("Unsupported file action.");
        }

        var root = Required(arguments, "root");
        var path = Required(arguments, "path");
        var version = Required(arguments, "expectedVersion");
        var components = path.Split('/');
        var invalidPath = Path.IsPathRooted(path)
            || path.Contains('\\')
            || components.Any(component => component is "" or "." or "..");

        if (invalidPath)
        {
            throw new ArgumentException("Choose a file inside the app sandbox.");
        }

        var parent = string.Join('/', components.SkipLast(1));
        var result = new JsonObject { ["root"] = root };

        if (operation == "files.write")
        {
            var encoded = arguments["contentBase64"]?.GetValue<string>() ?? throw new ArgumentException("File content is required.");
            byte[] bytes;

            try
            {
                bytes = Convert.FromBase64String(encoded);
                _ = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
            {
                throw new ArgumentException("Only UTF-8 text files can be edited.");
            }

            if (bytes.Length > 32 * 1024 || bytes.Contains((byte)0))
            {
                throw new ArgumentException("Text editing supports UTF-8 files up to 32 KB.");
            }

            result["directoryPath"] = parent.Length == 0 ? "." : parent;
            result["fileName"] = components[^1];
            result["contentBase64"] = encoded;
            result["overwrite"] = true;
            result["createDirectory"] = false;
        }
        else if (operation == "files.rename")
        {
            var name = Required(arguments, "name");
            var invalidName = name is "." or ".."
                || name.IndexOfAny(['/', '\\', '\0']) >= 0
                || name == components[^1];

            if (invalidName)
            {
                throw new ArgumentException("Enter a different file name without path separators.");
            }

            result["sourcePath"] = path;
            result["destinationPath"] = parent.Length == 0 ? name : parent + "/" + name;
            result["overwrite"] = false;
            result["createDirectory"] = false;
        }
        else
        {
            if (arguments["confirmed"]?.GetValue<bool>() != true)
            {
                throw new ArgumentException("Confirm deletion before deleting this file.");
            }

            result["path"] = path;
        }

        return new CliRemoteFileMutation(toolId, result, new JsonObject
        {
            ["root"] = root,
            ["path"] = path,
            ["expectedVersion"] = version,
            ["maxBytes"] = 1,
            ["encoding"] = "base64"
        });
    }

    private static string Required(JsonObject arguments, string name)
    {
        var value = arguments[name]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required. Reopen the file and try again.");
        }

        return value;
    }
}
