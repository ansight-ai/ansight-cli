using Ansight.Infrastructure.Extensions;

namespace Ansight.Cli.Extensions;

internal interface ICliExtensionInvoker
{
    object? Invoke(string operation, object?[] arguments);
}
