namespace Ansight.Host.Runtime.State;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Pairing.Models;
using Ansight.Infrastructure.Logging;

internal sealed class BoundedDeduplicationSet<T> where T : notnull
{
    private readonly int capacity;
    private readonly Queue<T> values = new();
    private readonly HashSet<T> lookup;

    public BoundedDeduplicationSet(int capacity, IEqualityComparer<T>? comparer = null)
    {
        this.capacity = Math.Max(1, capacity);
        lookup = new HashSet<T>(comparer);
    }

    public bool Add(T value)
    {
        if (!lookup.Add(value))
        {
            return false;
        }

        values.Enqueue(value);
        while (values.Count > capacity)
        {
            lookup.Remove(values.Dequeue());
        }

        return true;
    }

    public void Clear()
    {
        values.Clear();
        lookup.Clear();
    }
}
