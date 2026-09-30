namespace Ansight.Host.Cloud;
public interface ICloudService
{
    bool IsConfigured { get; }

    ISessionService Sessions { get; }

    IAppOperations Apps { get; }

    IAccountOperations Accounts { get; }

    IAppGraphOperations AppGraphs { get; }

    ITrendsOperations Trends { get; }

    ITestRunOperations Tests { get; }

    IRunnerOperations Runners { get; }
}
