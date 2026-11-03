using global::VectorData.ConformanceTests;
using global::VectorData.ConformanceTests.ModelTests;
using global::VectorData.ConformanceTests.Support;
using global::VectorData.ConformanceTests.TypeTests;
using Microsoft.Extensions.VectorData;
using OneIndex.VectorData;

namespace OneIndex.VectorData.Tests.Conformance;

/// <summary>The store the shared conformance suite runs against: one directory per test run.</summary>
public sealed class OneIndexTestStore : TestStore
{
    public static OneIndexTestStore Instance { get; } = new();

    private string _dir = string.Empty;

    public override string DefaultDistanceFunction => DistanceFunction.CosineSimilarity;
    public override string DefaultIndexKind => IndexKind.Hnsw;
    public override bool VectorsComparable => true;
    public override bool SupportsScoreThreshold => true;

    protected override async Task StartAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "oneindex-vd-conf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        DefaultVectorStore = new OneIndexVectorStore(_dir);
        await Task.CompletedTask;
    }

    protected override Task StopAsync()
    {
        (DefaultVectorStore as IDisposable)?.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        return Task.CompletedTask;
    }
}

public sealed class CollectionManagement(CollectionManagement.Fx f) : CollectionManagementTests<string>(f), IClassFixture<CollectionManagement.Fx>
{
    public sealed class Fx : VectorStoreFixture { public override TestStore TestStore => OneIndexTestStore.Instance; }
}

public sealed class Distance(Distance.Fx f) : DistanceFunctionTests<string>(f), IClassFixture<Distance.Fx>
{
    public sealed class Fx : DistanceFunctionTests<string>.Fixture
    {
        public override TestStore TestStore => OneIndexTestStore.Instance;
    }
}

public sealed class IndexKinds(IndexKinds.Fx f) : IndexKindTests<string>(f), IClassFixture<IndexKinds.Fx>
{
    public sealed class Fx : IndexKindTests<string>.Fixture
    {
        public override TestStore TestStore => OneIndexTestStore.Instance;
    }
}

public sealed class Filters(Filters.Fx f) : FilterTests<string>(f), IClassFixture<Filters.Fx>
{
    public sealed class Fx : FilterTests<string>.Fixture
    {
        // The adapter has no dynamic (dictionary) collections.
        public override bool TestDynamic => false;

        public override TestStore TestStore => OneIndexTestStore.Instance;
    }
}

public sealed class BasicModel(BasicModel.Fx f) : BasicModelTests<string>(f), IClassFixture<BasicModel.Fx>
{
    public sealed class Fx : BasicModelTests<string>.Fixture
    {
        public override TestStore TestStore => OneIndexTestStore.Instance;
    }
}

public sealed class NoData(NoData.Fx f) : NoDataModelTests<string>(f), IClassFixture<NoData.Fx>
{
    public sealed class Fx : NoDataModelTests<string>.Fixture
    {
        public override TestStore TestStore => OneIndexTestStore.Instance;
    }
}

public sealed class NoVector(NoVector.Fx f) : NoVectorModelTests<string>(f), IClassFixture<NoVector.Fx>
{
    public sealed class Fx : NoVectorModelTests<string>.Fixture
    {
        public override TestStore TestStore => OneIndexTestStore.Instance;
    }
}

public sealed class MultiVector(MultiVector.Fx f) : MultiVectorModelTests<string>(f), IClassFixture<MultiVector.Fx>
{
    public sealed class Fx : MultiVectorModelTests<string>.Fixture
    {
        public override TestStore TestStore => OneIndexTestStore.Instance;
    }
}

public sealed class KeyTypes(KeyTypes.Fx f) : KeyTypeTests(f), IClassFixture<KeyTypes.Fx>
{
    public sealed class Fx : KeyTypeTests.Fixture
    {
        public override TestStore TestStore => OneIndexTestStore.Instance;
    }
}

public sealed class Hybrid(Hybrid.Fx1 a, Hybrid.Fx2 b) : HybridSearchTests<string>(a, b), IClassFixture<Hybrid.Fx1>, IClassFixture<Hybrid.Fx2>
{
    public sealed class Fx1 : HybridSearchTests<string>.VectorAndStringFixture
    {
        public override TestStore TestStore => OneIndexTestStore.Instance;
    }

    public sealed class Fx2 : HybridSearchTests<string>.MultiTextFixture
    {
        public override TestStore TestStore => OneIndexTestStore.Instance;
    }
}
