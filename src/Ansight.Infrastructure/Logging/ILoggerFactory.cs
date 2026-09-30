using System;

namespace Ansight.Infrastructure.Logging;

	public interface ILoggerFactory : IDisposable
	{
		ILogger Create(string tag);
	}
