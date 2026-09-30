using Xunit;

// Host command tests start process-wide listeners and mutate shared runtime defaults.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
