namespace Ansight.Host.Runtime.Automation;

internal sealed record BoundedTextReadResult(string Text, bool ExceededLimit);
