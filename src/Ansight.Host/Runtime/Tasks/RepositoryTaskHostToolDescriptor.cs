namespace Ansight.Host.Runtime.Tasks;

internal enum RepositoryTaskHostToolApplicability
{
    SessionBound
}

internal sealed record RepositoryTaskHostToolDescriptor(
    string ToolName,
    string ApiFeatureName,
    string ApiMethodName,
    RepositoryTaskHostToolApplicability Applicability);

internal delegate RepositoryTaskHostToolDescriptor? RepositoryTaskHostToolResolver(string toolName);
