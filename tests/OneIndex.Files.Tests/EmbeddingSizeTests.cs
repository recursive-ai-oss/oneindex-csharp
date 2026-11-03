using System.Security.Cryptography;
using System.Text;
using OneIndex.Files.Tool;
using OneIndex.Vectors;

namespace OneIndex.Files.Tests;

public class EmbeddingSizeTests : IDisposable
{
    private readonly TempFolder _t = new();

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    // A deterministic stand-in for a 768-dimension model: a unit vector made from the words of the text.
    private sealed class WideEmbedder(int dimension) : IEmbedder
    {
        public string ModelId => "test-wide-" + dimension;

        public int Dimension => dimension;

        public bool IsNormalized => true;

        public void Embed(IReadOnlyList<string> texts, EmbeddingRole role, Span<float> destination, CancellationToken cancellationToken = default)
        {
            destination.Clear();
            for (int i = 0; i < texts.Count; i++)
            {
                Span<float> row = destination.Slice(i * dimension, dimension);
                foreach (string w in texts[i].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(w.ToLowerInvariant()));
                    row[((h[0] << 8) | h[1]) % dimension] += 1f;
                    row[((h[2] << 8) | h[3]) % dimension] -= 0.5f;
                }

                double norm = 0;
                foreach (float x in row)
                {
                    norm += x * x;
                }

                float inv = norm > 0 ? (float)(1 / Math.Sqrt(norm)) : 1f;
                for (int d = 0; d < row.Length; d++)
                {
                    row[d] *= inv;
                }
            }
        }

        public ValueTask EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, Memory<float> destination, CancellationToken cancellationToken = default)
        {
            Embed(texts, role, destination.Span, cancellationToken);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void AnEmbedderOf768DimensionsSizesThePassageVectorAndHybridSearchWorks()
    {
        string root = _t.Sub("root");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "The harbour lights glow over the quiet water while fishing boats return before dawn.");
        File.WriteAllText(Path.Combine(root, "b.txt"), "Compilers translate programs into machine code and optimise the loops they find.");
        string indexPath = _t.Sub("index");
        var embedder = new WideEmbedder(768);
        using (SearchIndex<FilePassage> index = FolderIndexer.OpenIndex(indexPath, embedder))
        {
            Assert.True(index.Schema.TryGetField(nameof(FilePassage.Embedding), out var field));
            Assert.Equal(768, field!.Dimensions);
            FolderIndexResult result = FolderIndexer.Run(index, root);
            Assert.Equal(2, result.Added);
            var hit = index.Search("fishing boats water").Hybrid().Take(1).Execute();
            Assert.Equal("a.txt", hit[0].Document.FileName);
        }

        // The size is part of the index: an embedder of another size is refused instead of corrupting it.
        Assert.ThrowsAny<Exception>(() => FolderIndexer.OpenIndex(indexPath, new WideEmbedder(384)).Dispose());
    }
}
