namespace Ansight.Host.Cloud;

internal interface ICloudCapabilityProvider
{
    IAppOperations Apps { get; }

    IAccountOperations Accounts { get; }

    IAppGraphOperations AppGraphs { get; }

    ITrendsOperations Trends { get; }

    ITestRunOperations Tests { get; }

    IRunnerOperations Runners { get; }
}
