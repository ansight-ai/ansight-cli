using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ansight.Host.Runtime.RepositoryContracts;

internal static class RepositoryModuleContractArtifacts
{
    public const int ContractVersion = 2;
    public const string TaskDefinitionSchemaId = "https://ansight.dev/schemas/repository-task.v2.schema.json";
    public const string TriggerDefinitionSchemaId = "https://ansight.dev/schemas/repository-trigger.v2.schema.json";

    private static readonly Assembly assembly = typeof(RepositoryModuleContractArtifacts).Assembly;
    private static readonly Lazy<string> taskDefinitionSchema = new(() => ReadText("ansight-task.schema.json"));
    private static readonly Lazy<string> triggerDefinitionSchema = new(() => ReadText("ansight-trigger.schema.json"));
    private static readonly Lazy<string> taskRuntimeModule = new(() => ReadText("ansight-task.js"));
    private static readonly Lazy<string> taskTypeDefinitions = new(() => ReadText("ansight-task.d.ts"));
    private static readonly Lazy<string> triggerTypeDefinitions = new(() => ReadText("ansight-trigger.d.ts"));

    public static JsonObject GetTaskDefinitionSchema()
        => ParseObject(taskDefinitionSchema.Value, "task definition schema");

    public static JsonObject GetTriggerDefinitionSchema()
        => ParseObject(triggerDefinitionSchema.Value, "trigger definition schema");

    public static string GetTaskRuntimeModule() => taskRuntimeModule.Value;

    // Keep hover documentation out of Node's command-line script, which has a 32 KiB limit on Windows.
    public static string GetTaskRuntimeConstants()
        => Regex.Replace(taskRuntimeModule.Value, @"/\*\*.*?\*/", string.Empty, RegexOptions.Singleline).Trim();

    public static string GetTaskTypeDefinitions() => taskTypeDefinitions.Value;

    public static string GetTriggerTypeDefinitions() => triggerTypeDefinitions.Value;

    private static JsonObject ParseObject(string source, string description)
        => JsonNode.Parse(source) as JsonObject
           ?? throw new InvalidDataException($"The embedded Ansight {description} is not a JSON object.");

    private static string ReadText(string fileName)
    {
        var suffix = $".Runtime.RepositoryContracts.{fileName}";
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));
        if (resourceName is null)
        {
            throw new InvalidOperationException($"The embedded Ansight contract artifact '{fileName}' was not found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException(
                               $"The embedded Ansight contract artifact '{fileName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
