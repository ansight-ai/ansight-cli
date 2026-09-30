using System;
using Ansight.Infrastructure.Logging;

namespace Ansight.Infrastructure.Logging;

	public interface IApplicationLogBuffer
	{
		event EventHandler<LogBufferChangedEventArgs>? OnLogsChanged;

		int Capacity { get; }

		IReadOnlyList<string> Logs { get; }
	}
