using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Automation;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Runtime.RepositoryContracts;

internal static class RepositoryModuleContractBuilder
{
    public static JsonObject BuildTask(RepositoryTaskDefinition task, bool includeDefinitions)
    {
        ArgumentNullException.ThrowIfNull(task);
        var descriptor = ReadDescriptor(task.ModulePath, "task", "Task");
        var result = new JsonObject
        {
            ["contractVersion"] = RepositoryModuleContractArtifacts.ContractVersion,
            ["moduleType"] = "task",
            ["moduleId"] = task.TaskId,
            ["modulePath"] = ToRepositoryRelativePath(task.RepositoryRootPath, task.ModulePath),
            ["descriptor"] = descriptor,
            ["schemas"] = new JsonObject
            {
                ["definitionSchemaId"] = RepositoryModuleContractArtifacts.TaskDefinitionSchemaId,
                ["input"] = task.InputSchema.DeepClone(),
                ["output"] = task.OutputSchema?.DeepClone()
            },
            ["declaredHostTools"] = ToStringArray(task.DeclaredHostTools.Keys)
        };
        if (includeDefinitions)
        {
            result["definitionSchema"] = RepositoryModuleContractArtifacts.GetTaskDefinitionSchema();
            result["typeDefinitions"] = RepositoryModuleContractArtifacts.GetTaskTypeDefinitions();
            result["runtimeModule"] = new JsonObject
            {
                ["fileName"] = "ansight-task.js",
                ["source"] = RepositoryModuleContractArtifacts.GetTaskRuntimeModule()
            };
        }

        return result;
    }

    public static JsonObject BuildTrigger(RepositoryAutomationTriggerDefinition trigger, bool includeDefinitions)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        var modulePath = trigger.Automation.EntrypointPath
                         ?? throw new InvalidDataException(
                             $"Trigger '{trigger.TriggerId}' does not have a TypeScript module path.");
        var descriptor = ReadDescriptor(modulePath, "trigger", "Trigger");
        var result = new JsonObject
        {
            ["contractVersion"] = RepositoryModuleContractArtifacts.ContractVersion,
            ["moduleType"] = "trigger",
            ["moduleId"] = trigger.TriggerId,
            ["modulePath"] = ToRepositoryRelativePath(trigger.RepositoryRootPath, modulePath),
            ["descriptor"] = descriptor,
            ["schemas"] = new JsonObject
            {
                ["definitionSchemaId"] = RepositoryModuleContractArtifacts.TriggerDefinitionSchemaId,
                ["eventPayload"] = trigger.EventSchema?.DeepClone()
            }
        };
        if (includeDefinitions)
        {
            result["definitionSchema"] = RepositoryModuleContractArtifacts.GetTriggerDefinitionSchema();
            result["typeDefinitions"] = RepositoryModuleContractArtifacts.GetTriggerTypeDefinitions();
        }

        return result;
    }

    private static JsonObject ReadDescriptor(string modulePath, string exportName, string moduleKind)
    {
        var source = File.ReadAllText(modulePath);
        var descriptorJson = RepositoryModuleDescriptorReader.ExtractObject(
            source,
            modulePath,
            exportName,
            moduleKind);
        return JsonNode.Parse(descriptorJson) as JsonObject
               ?? throw new InvalidDataException(
                   $"{moduleKind} module '{modulePath}' descriptor is not a JSON object.");
    }

    private static JsonArray ToStringArray(IEnumerable<string> values)
        => new(values
            .Order(StringComparer.Ordinal)
            .Select(value => (JsonNode?)value)
            .ToArray());

    private static string ToRepositoryRelativePath(string repositoryRootPath, string modulePath)
        => Path.GetRelativePath(repositoryRootPath, modulePath)
            .Replace(Path.DirectorySeparatorChar, '/');
}
