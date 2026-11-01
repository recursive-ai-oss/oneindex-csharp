using HNSW.Net;

namespace OneIndex.VectorBench;

/// <summary>curiosity-ai HNSW 26.9.5494, through its public API as the package README documents it.</summary>
internal sealed class CuriosityEngine(int m, int efConstruction) : IEngine
{
    private SmallWorld<float[], float>? _graph;
    private SmallWorldParameters? _parameters;

    public string Name => "curiosity";

    public long IndexBytes => -1;

    public void Build(float[] data, int count, int dimensions)
    {
        var vectors = new List<float[]>(count);
        for (int i = 0; i < count; i++)
        {
            vectors.Add(data.AsSpan(i * dimensions, dimensions).ToArray());
        }

        _parameters = new SmallWorldParameters { M = m, LevelLambda = 1 / Math.Log(m), ConstructionPruning = efConstruction, EfSearch = 50 };
        _graph = new SmallWorld<float[], float>(CosineDistance.SIMDForUnits, DefaultRandomGenerator.Instance, _parameters);
        _graph.AddItems(vectors);
    }

    public int Search(float[] query, int k, int ef, int[] ids)
    {
        int n = 0;
        foreach (var r in _graph!.KNNSearch(query, k))
        {
            ids[n++] = r.Id;
        }

        return n;
    }

    public void Dispose()
    {
    }
}
