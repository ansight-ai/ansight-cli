using System;

namespace Ansight.Infrastructure.Logging;

public interface IMutableApplicationLogBuffer : IApplicationLogBuffer
{
	void Append(string content);

	void Append(IReadOnlyList<string> lines);

	void Clear();

	void SetCapacity(int capacity);
}