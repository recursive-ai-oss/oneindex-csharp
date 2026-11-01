using HnswNet;

namespace OneIndex.VectorBench;

/// <summary>Hnsw.Net 0.1.4, through its public API as the package README documents it.</summary>
internal sealed class HnswNetEngine(int m, int efConstruction) : IEngine
{
    private HnswIndex? _index;

    public string Name => "hnswnet";

    public long IndexBytes
    {
        get
        {
            if (_index is null)
            {
                return -1;
            }

            using var ms = new MemoryStream();
            _index.Save(ms);
            return ms.Length;
        }
    }

    public void Build(float[] data, int count, int dimensions)
    {
        _index = new HnswIndex(dimensions, DistanceMetric.Cosine, m: m, efConstruction: efConstruction, seed: 42);
        for (int i = 0; i < count; i++)
        {
            _index.Add(i, data.AsSpan(i * dimensions, dimensions).ToArray());
        }
    }

    public int Search(float[] query, int k, int ef, int[] ids)
    {
        _index!.Ef = ef;
        int n = 0;
        foreach ((long id, float _) in _index.Search(query, k))
        {
            ids[n++] = (int)id;
        }

        return n;
    }

    public void Dispose() => _index?.Dispose();
}
