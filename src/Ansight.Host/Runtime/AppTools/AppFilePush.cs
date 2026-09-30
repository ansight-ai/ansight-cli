using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.AppTools;

public static class AppFilePush
{
    public const string ToolId = "files.push_file";
    public const string LocalFilePathArgument = "localFilePath";

    public static AppFilePushRequest CreateRequest(JsonObject? arguments)
    {
        var localFilePath = ReadRequiredString(arguments, LocalFilePathArgument);
        var directoryPath = ReadRequiredString(arguments, "directoryPath");
        var sandboxRoot = ReadOptionalString(arguments, "root");
        var fileName = ReadOptionalString(arguments, "fileName");
        var overwrite = ReadBoolean(arguments, "overwrite", defaultValue: false);
        var createDirectory = ReadBoolean(arguments, "createDirectory", defaultValue: true);

        return new AppFilePushRequest(
            localFilePath,
            directoryPath,
            sandboxRoot,
            fileName,
            overwrite,
            createDirectory);
    }

    public static async Task<JsonObject> CreateRemoteArgumentsAsync(
        AppFilePushRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var localFilePath = NormalizeExistingLocalFilePath(request.LocalFilePath);
        var fileName = NormalizeFileName(request.FileName, localFilePath);
        var directoryPath = NormalizeRequiredText(request.DirectoryPath, "directoryPath");
        var bytes = await File.ReadAllBytesAsync(localFilePath, cancellationToken).ConfigureAwait(false);

        var arguments = new JsonObject
        {
            ["directoryPath"] = directoryPath,
            ["fileName"] = fileName,
            ["contentBase64"] = Convert.ToBase64String(bytes),
            ["overwrite"] = request.Overwrite,
            ["createDirectory"] = request.CreateDirectory
        };

        if (!string.IsNullOrWhiteSpace(request.SandboxRoot))
        {
            arguments["root"] = request.SandboxRoot.Trim();
        }

        return arguments;
    }

    public static JsonObject CreateHostArgumentTemplate(string? localFilePath = null, JsonObject? existingArguments = null)
    {
        var template = new JsonObject
        {
            [LocalFilePathArgument] = localFilePath?.Trim() ?? string.Empty,
            ["root"] = ReadOptionalString(existingArguments, "root") ?? string.Empty,
            ["directoryPath"] = ReadOptionalString(existingArguments, "directoryPath") ?? string.Empty,
            ["fileName"] = ResolveTemplateFileName(localFilePath, existingArguments),
            ["overwrite"] = ReadBoolean(existingArguments, "overwrite", defaultValue: false),
            ["createDirectory"] = ReadBoolean(existingArguments, "createDirectory", defaultValue: true)
        };

        return template;
    }

    public static bool IsPushFileTool(string? toolId)
        => string.Equals(toolId, ToolId, StringComparison.Ordinal);

    private static string NormalizeExistingLocalFilePath(string localFilePath)
    {
        var normalizedPath = NormalizeRequiredText(localFilePath, LocalFilePathArgument);
        var fullPath = Path.GetFullPath(normalizedPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The local file '{fullPath}' does not exist.", fullPath);
        }

        return fullPath;
    }

    private static string NormalizeFileName(string? fileName, string localFilePath)
    {
        var resolvedFileName = string.IsNullOrWhiteSpace(fileName)
            ? Path.GetFileName(localFilePath)
            : fileName.Trim();
        if (string.IsNullOrWhiteSpace(resolvedFileName))
        {
            throw new InvalidOperationException("A destination fileName could not be inferred from localFilePath.");
        }

        if (Path.IsPathRooted(resolvedFileName) ||
            resolvedFileName.Contains('/') ||
            resolvedFileName.Contains('\\') ||
            resolvedFileName is "." or ".." ||
            !string.Equals(Path.GetFileName(resolvedFileName), resolvedFileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("fileName must be a file name, not a path.");
        }

        if (resolvedFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("fileName contains invalid file name characters.");
        }

        return resolvedFileName;
    }

    private static string ResolveTemplateFileName(string? localFilePath, JsonObject? existingArguments)
    {
        var existingFileName = ReadOptionalString(existingArguments, "fileName");
        if (!string.IsNullOrWhiteSpace(existingFileName))
        {
            return existingFileName;
        }

        if (string.IsNullOrWhiteSpace(localFilePath))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFileName(localFilePath.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeRequiredText(string value, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{argumentName} is required.");
        }

        return value.Trim();
    }

    private static string ReadRequiredString(JsonObject? arguments, string propertyName)
    {
        var value = ReadOptionalString(arguments, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{propertyName} is required.");
        }

        return value;
    }

    private static string? ReadOptionalString(JsonObject? arguments, string propertyName)
    {
        if (arguments?[propertyName] is null)
        {
            return null;
        }

        var value = arguments[propertyName]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool ReadBoolean(JsonObject? arguments, string propertyName, bool defaultValue)
    {
        if (arguments?[propertyName] is not JsonValue value)
        {
            return defaultValue;
        }

        if (value.TryGetValue<bool>(out var boolValue))
        {
            return boolValue;
        }

        var text = value.ToString();
        if (bool.TryParse(text, out boolValue))
        {
            return boolValue;
        }

        if (text == "1")
        {
            return true;
        }

        if (text == "0")
        {
            return false;
        }

        throw new InvalidOperationException($"{propertyName} must be a boolean.");
    }
}
