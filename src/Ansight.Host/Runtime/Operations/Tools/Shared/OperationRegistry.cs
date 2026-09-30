using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Runtime.Operations.Tools.Shared;

internal sealed class OperationRegistry
{
    private readonly IReadOnlyList<OperationRegistration> registrations;
    private readonly IReadOnlyDictionary<string, OperationRegistration> registrationsByName;
    private readonly IReadOnlyDictionary<string, RepositoryTaskHostToolDescriptor> repositoryTaskToolsByName;

    public OperationRegistry(IEnumerable<OperationRegistration> registrations)
    {
        var registrationArray = registrations.ToArray();
        var indexedTools = new Dictionary<string, IOperation>(StringComparer.Ordinal);
        var repositoryTaskTools = new Dictionary<string, RepositoryTaskHostToolDescriptor>(StringComparer.Ordinal);
        var repositoryTaskMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in registrationArray)
        {
            var tool = registration.Tool;
            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                throw new InvalidOperationException("Operation names must be non-empty.");
            }

            if (indexedTools.ContainsKey(tool.Name))
            {
                throw new InvalidOperationException($"An operation named '{tool.Name}' has already been registered.");
            }

            indexedTools.Add(tool.Name, tool);
            if (registration.TaskApplicability is not { } taskApplicability)
            {
                continue;
            }

            var apiMethod = RepositoryJavaScriptApiMethods.ResolveHostApiMethod(tool.Name);
            var apiPath = $"{apiMethod.FeatureName}.{apiMethod.MethodName}";
            if (!repositoryTaskMethods.Add(apiPath))
            {
                throw new InvalidOperationException(
                    $"Repository task API method '{apiPath}' is mapped from more than one host tool.");
            }

            repositoryTaskTools.Add(
                tool.Name,
                new RepositoryTaskHostToolDescriptor(
                    tool.Name,
                    apiMethod.FeatureName,
                    apiMethod.MethodName,
                    taskApplicability));
        }

        this.registrations = registrationArray;
        registrationsByName = this.registrations.ToDictionary(
            static registration => registration.Tool.Name,
            StringComparer.Ordinal);
        repositoryTaskToolsByName = repositoryTaskTools;
    }

    public JsonArray BuildDefinitions()
        => PayloadJson.CreateJsonArray(registrations
            .Select(registration => (JsonNode?)registration.Tool.Definition));

    public bool TryGet(string toolName, out IOperation? tool)
    {
        if (registrationsByName.TryGetValue(toolName, out var registration))
        {
            tool = registration.Tool;
            return true;
        }

        tool = null;
        return false;
    }

    public RepositoryTaskHostToolDescriptor? ResolveRepositoryTaskTool(string toolName)
        => repositoryTaskToolsByName.GetValueOrDefault(toolName);

    public IEnumerable<RepositoryTaskHostToolDescriptor> GetRepositoryTaskTools()
        => repositoryTaskToolsByName.Values;

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildRepositoryTaskApiSuites()
        => repositoryTaskToolsByName.Values
            .GroupBy(descriptor => descriptor.ApiFeatureName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, string>)group.ToDictionary(
                    descriptor => descriptor.ApiMethodName,
                    descriptor => descriptor.ToolName,
                    StringComparer.Ordinal),
                StringComparer.Ordinal);

    public bool Contains(string toolName)
        => registrationsByName.ContainsKey(toolName);

}

internal sealed record OperationRegistration(
    IOperation Tool,
    RepositoryTaskHostToolApplicability? TaskApplicability)
{
    public static OperationRegistration HostOnly(IOperation tool)
        => new(tool, TaskApplicability: null);

    public static OperationRegistration TaskSessionBound(IOperation tool)
        => new(tool, RepositoryTaskHostToolApplicability.SessionBound);
}
