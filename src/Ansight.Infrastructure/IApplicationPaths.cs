namespace Ansight.Infrastructure;

/// <summary>
/// The paths that this application uses.
/// </summary>
public interface IApplicationPaths
{
    string ApplicationDataPath { get; }

    string ApplicationLogsPath { get; }

    string ApplicationTempPath { get; }
}
