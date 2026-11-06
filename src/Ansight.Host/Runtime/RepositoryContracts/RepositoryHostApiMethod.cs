namespace Ansight.Host.Runtime.RepositoryContracts;

internal sealed record RepositoryHostApiMethod(
    string FeatureName,
    string MethodName,
    string ToolName);
