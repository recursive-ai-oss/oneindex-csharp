using System.IO.Compression;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OneIndex.Codecs;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Bench;

/// <summary>
/// Which compression to use for stored-source blocks (docs/stored-source.md): the OneIndex LZ against the BCL's deflate, zlib and
/// Brotli on real blocks, 32 KB of encoded documents each. Set ONEINDEX_WIKIPEDIA to a JSON-lines file of {"id", "title", "text"}
/// documents. Each codec prints its compression ratio from setup; the benchmarks time compressing and decompressing every block.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class StoredCompressionBenchmarks
{
    private readonly List<byte[]> _raw = [];
    private readonly List<byte[]> _packed = [];
    private readonly LzCompressor _lz = new();
    private byte[] _scratch = [];

    /// <summary>The codec under test.</summary>
    [Params("lz", "deflate-fastest", "deflate-optimal", "zlib-optimal", "brotli-1", "brotli-4")]
    public string Codec { get; set; } = "lz";

    /// <summary>Builds the blocks and compresses them once to report the ratio.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = Environment.GetEnvironmentVariable("ONEINDEX_WIKIPEDIA") ?? throw new InvalidOperationException("Set ONEINDEX_WIKIPEDIA to a JSON-lines corpus.");
        var index = new Dictionary<string, int>(StringComparer.Ordinal) { ["id"] = 0, ["title"] = 1, ["text"] = 2 };
        var records = new ByteBuilder(1 << 16);
        var ends = new List<int>();
        var source = new ByteBuilder(1024);
        foreach (string line in File.ReadLines(path).Take(60_000))
        {
            using JsonDocument j = JsonDocument.Parse(line);
            var d = new Document()
                .Add("id", j.RootElement.GetProperty("id").GetString()!)
                .Add("title", j.RootElement.GetProperty("title").GetString()!)
                .Add("text", j.RootElement.GetProperty("text").GetString()!);
            source.Clear();
            StoredDocumentFormat.Encode(d, index, source);
            records.WriteVarUInt((uint)source.Length);
            records.Write(source.Span);
            records.WriteVarUInt(0);
            ends.Add(records.Length);
            if (records.Length >= StoredSegmentCodec.TargetBlockBytes)
            {
                var raw = new ByteBuilder(records.Length + (ends.Count * 4));
                foreach (int e in ends)
                {
                    raw.WriteUInt32((uint)e);
                }

                raw.Write(records.Span);
                _raw.Add(raw.Span.ToArray());
                records.Clear();
                ends.Clear();
            }
        }

        _scratch = new byte[LzCompressor.MaxCompressedLength(1 << 20) + (1 << 16)];
        long rawBytes = 0;
        long packedBytes = 0;
        foreach (byte[] block in _raw)
        {
            byte[] p = Compress(block);
            _packed.Add(p);
            rawBytes += block.Length;
            packedBytes += p.Length;
        }

        Console.WriteLine($"stored-study codec={Codec} blocks={_raw.Count} raw={rawBytes} packed={packedBytes} ratio={(double)rawBytes / packedBytes:F3}");
    }

    /// <summary>Compresses every block.</summary>
    /// <returns>The total packed size.</returns>
    [Benchmark]
    public long CompressAll()
    {
        long total = 0;
        foreach (byte[] block in _raw)
        {
            total += Compress(block).Length;
        }

        return total;
    }

    /// <summary>Decompresses every block.</summary>
    /// <returns>The total raw size.</returns>
    [Benchmark]
    public long DecompressAll()
    {
        long total = 0;
        for (int i = 0; i < _packed.Count; i++)
        {
            total += Decompress(_packed[i], _scratch.AsSpan(0, _raw[i].Length));
        }

        return total;
    }

    private byte[] Compress(byte[] block)
    {
        switch (Codec)
        {
            case "lz":
                {
                    var b = new ByteBuilder(block.Length);
                    _lz.Compress(block, b);
                    return b.Span.ToArray();
                }

            case "deflate-fastest":
            case "deflate-optimal":
                {
                    using var ms = new MemoryStream();
                    using (var z = new DeflateStream(ms, Codec == "deflate-fastest" ? CompressionLevel.Fastest : CompressionLevel.Optimal, true))
                    {
                        z.Write(block);
                    }

                    return ms.ToArray();
                }

            case "zlib-optimal":
                {
                    using var ms = new MemoryStream();
                    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true))
                    {
                        z.Write(block);
                    }

                    return ms.ToArray();
                }

            default:
                {
                    var dest = new byte[BrotliEncoder.GetMaxCompressedLength(block.Length)];
                    BrotliEncoder.TryCompress(block, dest, out int written, Codec == "brotli-1" ? 1 : 4, 22);
                    return dest.AsSpan(0, written).ToArray();
                }
        }
    }

    private long Decompress(byte[] packed, Span<byte> destination)
    {
        switch (Codec)
        {
            case "lz":
                return LzDecompressor.TryDecompress(packed, destination) ? destination.Length : throw new InvalidOperationException();
            case "deflate-fastest":
            case "deflate-optimal":
                {
                    using var z = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
                    z.ReadExactly(destination);
                    return destination.Length;
                }

            case "zlib-optimal":
                {
                    using var z = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
                    z.ReadExactly(destination);
                    return destination.Length;
                }

            default:
                BrotliDecoder.TryDecompress(packed, destination, out int written);
                return written;
        }
    }
}

/// <summary>
/// Indicative timings of the doc-value structures on 300,000 documents in one segment: column access and scans, a range query served
/// by the point index against the same range served by numeric terms (the index written without the point codec), and a stored
/// document fetch.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class DocValueBenchmarks
{
    private const int Docs = 300_000;
    private IndexSnapshot _points = null!;
    private IndexSnapshot _terms = null!;
    private IndexSearcher _pointSearcher = null!;
    private IndexSearcher _termSearcher = null!;
    private SegmentColumn _price = null!;
    private SegmentColumn _category = null!;
    private SegmentStoredFields _stored = null!;
    private long[] _buffer = new long[4096];
    private int[] _fetch = [];

    /// <summary>Writes the same documents twice, with and without the point codec.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Schema schema = new SchemaBuilder().Id("id").Integral("price").Date("ts")
            .Add(new FieldDefinition("category", FieldType.Keyword) { Column = true }).Text("title").Build();
        (IndexSnapshot a, IndexSearcher sa) = Build(schema, SegmentCodecs.CreateDefault());
        (IndexSnapshot b, IndexSearcher sb) = Build(schema, [new TextSegmentCodec { IndexNumericTerms = true }, new ColumnSegmentCodec(), new StoredSegmentCodec()]);
        _points = a;
        _pointSearcher = sa;
        _terms = b;
        _termSearcher = sb;
        SegmentReader seg = a.GetSegment(0);
        _price = (SegmentColumn)seg.GetPart<IColumnSource>()!.GetColumn("price")!;
        _category = (SegmentColumn)seg.GetPart<IColumnSource>()!.GetColumn("category")!;
        _stored = (SegmentStoredFields)seg.GetPart<IStoredFieldsSource>()!.StoredFields;
        var rng = new Random(5);
        _fetch = [.. Enumerable.Range(0, 1000).Select(_ => rng.Next(Docs))];
    }

    /// <summary>Releases the indexes.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _points.Dispose();
        _terms.Dispose();
    }

    /// <summary>Sums the integer column with one random-access read per document.</summary>
    /// <returns>The sum.</returns>
    [Benchmark]
    public long ColumnRandomAccess()
    {
        long sum = 0;
        for (int d = 0; d < Docs; d++)
        {
            sum += _price.GetInt64(d);
        }

        return sum;
    }

    /// <summary>Sums the integer column with bulk decoding.</summary>
    /// <returns>The sum.</returns>
    [Benchmark]
    public long ColumnBulkScan()
    {
        long sum = 0;
        for (int at = 0; at < Docs; at += _buffer.Length)
        {
            Span<long> run = _buffer.AsSpan(0, Math.Min(_buffer.Length, Docs - at));
            _price.ReadValues(at, run);
            foreach (long v in run)
            {
                sum += v;
            }
        }

        return sum;
    }

    /// <summary>Sums the keyword ordinals of the category column.</summary>
    /// <returns>The sum.</returns>
    [Benchmark]
    public long KeywordOrdinalScan()
    {
        long sum = 0;
        for (int d = 0; d < Docs; d++)
        {
            sum += _category.GetOrdinal(d);
        }

        return sum;
    }

    /// <summary>A range matching about 1% of documents, through the point index.</summary>
    /// <returns>The match count.</returns>
    [Benchmark]
    public long PointRange1Percent() => Count(_pointSearcher, 0, 10_000);

    /// <summary>The same range through numeric terms.</summary>
    /// <returns>The match count.</returns>
    [Benchmark]
    public long TermRange1Percent() => Count(_termSearcher, 0, 10_000);

    /// <summary>A range matching about half the documents, through the point index.</summary>
    /// <returns>The match count.</returns>
    [Benchmark]
    public long PointRange50Percent() => Count(_pointSearcher, 0, 500_000);

    /// <summary>The same range through numeric terms.</summary>
    /// <returns>The match count.</returns>
    [Benchmark]
    public long TermRange50Percent() => Count(_termSearcher, 0, 500_000);

    /// <summary>Fetches 1,000 random documents from stored source.</summary>
    /// <returns>The number of fields read.</returns>
    [Benchmark]
    public int StoredFetch1000()
    {
        int fields = 0;
        foreach (int d in _fetch)
        {
            fields += _stored.GetDocument(d).Fields.Count;
        }

        return fields;
    }

    private static long Count(IndexSearcher searcher, long lo, long hi)
        => searcher.Search(new RangeQuery("price", FieldValue.FromInt64(lo), FieldValue.FromInt64(hi)), 1, countExactly: true).TotalHits;

    private static (IndexSnapshot, IndexSearcher) Build(Schema schema, IReadOnlyList<ISegmentCodec> codecs)
    {
        var storage = new MemoryStorage();
        using (IndexWriter w = IndexWriter.Open(storage, schema, new IndexWriterOptions { Codecs = codecs, MaxBufferedDocuments = Docs + 1, AutoMerge = false }))
        {
            var rng = new Random(1);
            for (int i = 0; i < Docs; i++)
            {
                w.Upsert(new Document().Add("id", "d" + i).Add("price", (long)rng.Next(0, 1_000_000)).Add("ts", DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000 + (i * 1000L)))
                    .Add("category", "c" + (i % 50)).Add("title", "item number " + i));
            }

            w.Commit();
        }

        IndexSnapshot snapshot = IndexSnapshot.Open(storage, codecs);
        return (snapshot, SnapshotSearch.CreateSearcher(snapshot));
    }
}
