namespace Ansight.Host.Runtime.Tasks;

internal sealed record RepositoryTaskHostToolDescriptor(
    string ToolName,
    string ApiFeatureName,
    string ApiMethodName,
    RepositoryTaskHostToolApplicability Applicability);
