using System;

namespace Ansight.Infrastructure.Logging;

public interface ILogger
{
	/// <summary>
	/// The tag that will be attached to each line logged by this logger instance.
	/// </summary>
	string Tag { get; }

	void Log(string tag, string message, LogLevel logLevel);

	/// <summary>
	/// Writes an <see cref="LogLevel.Error"/> log entry.
	/// </summary>
	void Error(string message);

	/// <summary>
	/// Writes an <see cref="LogLevel.Warning"/> log entry.
	/// </summary>
	void Warning(string message);

	/// <summary>
	/// Writes an <see cref="LogLevel.Information"/> log entry.
	/// </summary>
	void Info(string message);

	/// <summary>
	/// Writes an <see cref="LogLevel.Debug"/> log entry.
	/// </summary>
	void Debug(string message);

	/// <summary>
	/// Writes an <see cref="LogLevel.Verbose"/> log entry.
	/// </summary>
	void Verbose(string message);

	/// <summary>
	/// Logs a new property value.
	/// </summary>
	void Property(string key, string value, string group = "");

	/// <summary>
	/// Logs an exception
	/// </summary>
	void Exception(Exception ex);

	/// <summary>
	/// Logs an analytics event.
	/// </summary>
	void Event(string eventName, IReadOnlyDictionary<string, object?>? properties = null);

	/// <summary>
	/// Logs an analytics event.
	/// </summary>
	void Event(string eventName, params LogEventParameter[] parameters);

	/// <summary>
	/// If formatted logging (IE: Tag, process, time) is currently suspended and the logger will capture raw
	/// </summary>
	bool IsFormattingSuspended { get; }

	/// <summary>
	/// Suspends log formatting until the <see cref="ISuspendLogFormattingScope"/> is disposed or <see cref="ResumeFormatting"/> is called
	/// </summary>
	ISuspendLogFormattingScope SuspendFormatting();

	void ResumeFormatting();
}
