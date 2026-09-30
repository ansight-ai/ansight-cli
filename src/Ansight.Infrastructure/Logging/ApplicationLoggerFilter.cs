using System;
using System.Collections.Generic;
using Ansight.Infrastructure.Logging;

namespace Ansight.Infrastructure.Logging;

public class ApplicationLoggerFilter : ILogFilter
{
    private LogLevel minimumLogLevel;
    private HashSet<string> tagFilters;

    public ApplicationLoggerFilter()
    {
        minimumLogLevel = LogLevel.Information;
        tagFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public LogLevel MinimumLogLevel => minimumLogLevel;

    public void SetMinimumLogLevel(LogLevel logLevel)
    {
        minimumLogLevel = logLevel;
    }

    public void SetTagFilters(IReadOnlyList<string> filters)
    {
        tagFilters = CreateFilters(filters);
    }

    private static HashSet<string> CreateFilters(IReadOnlyList<string> verboseTagFilters)
    {
        var filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (verboseTagFilters == null || verboseTagFilters.Count == 0)
        {
            return filters;
        }

        foreach (var filter in verboseTagFilters)
        {
            if (!string.IsNullOrWhiteSpace(filter))
            {
                filters.Add(filter);
            }
        }

        return filters;
    }

    public bool CanLog(string tag, string message, LogLevel logLevel)
    {
        if (logLevel < minimumLogLevel)
        {
            return false;
        }

        if (logLevel == LogLevel.Verbose
            && !string.IsNullOrEmpty(tag)
            && tagFilters.Count > 0)
        {
            if (!tagFilters.Contains(tag))
            {
                return false;
            }
        }

        return true;
    }
}
