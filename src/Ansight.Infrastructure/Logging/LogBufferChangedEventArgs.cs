using System;
namespace Ansight.Infrastructure.Logging;

	public class LogBufferChangedEventArgs : EventArgs
	{
		public LogBufferChangedEventArgs(IReadOnlyList<string> addedlines,
									    IReadOnlyList<string> removedLines)
		{
        Addedlines = addedlines ?? throw new ArgumentNullException(nameof(addedlines));
        RemovedLines = removedLines ?? throw new ArgumentNullException(nameof(removedLines));
    }

    public IReadOnlyList<string> Addedlines { get; }
    public IReadOnlyList<string> RemovedLines { get; }
}
