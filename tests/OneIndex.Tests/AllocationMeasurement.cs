namespace OneIndex.Tests;

/// <summary>
/// Test classes that measure allocations run in this collection, never beside other tests: a collection started by a
/// parallel test makes the per-thread allocation counter jump even when the measured thread allocates nothing.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurement
{
    /// <summary>The collection name used in <c>[Collection]</c> attributes.</summary>
    public const string Name = "Allocation measurement";
}
