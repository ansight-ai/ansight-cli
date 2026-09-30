using System;

namespace Ansight.Infrastructure.Logging;

public interface ILogFileProvider
{
    string LogDirectory { get; }

    string CurrentLogFile { get; }
}