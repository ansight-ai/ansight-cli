using Ansight.Infrastructure.Extensions;
namespace Ansight.Host.Tests.Unit.Extensions;

public sealed class InertOptionalExtension : IOptionalExtension
{
    private readonly Dictionary<string, int> counters;

    public InertOptionalExtension(object context)
    {
        counters = (Dictionary<string, int>)context;
        counters["constructed"] = counters.GetValueOrDefault("constructed") + 1;
    }

    public object GetService(Type contract) => this;

    public void Dispose() => counters["disposed"] = counters.GetValueOrDefault("disposed") + 1;
}
