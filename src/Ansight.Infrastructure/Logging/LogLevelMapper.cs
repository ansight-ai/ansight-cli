using System;
namespace Ansight.Infrastructure.Logging;

	public static class LogLevelMapper
	{
		public static LogLevel Map(string value, LogLevel fallbackValue = LogLevel.Information)
		{
        if (string.IsNullOrWhiteSpace(value))
        {
				return fallbackValue;
        }

			switch (value.ToLowerInvariant())
        {
            case "v":
            case "verbose":
                return LogLevel.Verbose;
            case "d":
            case "debug":
                return LogLevel.Debug;
            case "i":
            case "info":
            case "information":
                return LogLevel.Information;
            case "w":
            case "warning":
                return LogLevel.Warning;
            case "e":
            case "error":
                return LogLevel.Error;
            case "f":
            case "fatal":
                return LogLevel.Fatal;
            default:
                return fallbackValue;
        }
    }
	}
