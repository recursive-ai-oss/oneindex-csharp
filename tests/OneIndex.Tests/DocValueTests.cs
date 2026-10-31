using System.Buffers;
using System.Text;
using OneIndex.Codecs;
using OneIndex.Fuzz;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

/// <summary>Columns, the point index and stored source through the index writer: flush, merge with deletes, reopen.</summary>
public sealed class DocValueTests
{
    private static readonly Schema TestSchema = DocValuesFixture.CreateSchema();
    private static int s_cases;

    [Fact]
    public void IntStreamRoundTripsEveryShape()
    {
        var rng = new Random(11);
        int cases = 0;
        for (int round = 0; round < 400; round++)
        {
            int count = round switch { < 5 => round, < 20 => 1024 + round - 12, _ => rng.Next(0, 4000) };
            long[] values = new long[count];
            int shape = rng.Next(8);
            for (int i = 0; i < count; i++)
            {
                values[i] = shape switch
                {
                    0 => 42,
                    1 => rng.Next(0, 7),
                    2 => rng.NextInt64(),
                    3 => rng.Next(0, 1000) * 1_000_000L,
                    4 => (i / 3) * 5L,
                    5 => rng.Next(4) switch { 0 => long.MinValue, 1 => long.MaxValue, 2 => 0, _ => -1 },
                    6 => 1_700_000_000_000 + (i * 17L),
                    _ => (long)rng.Next(5) << 55,
                };
            }

            var sink = new ByteBuilder();
            sink.Write(new byte[round % 5]); // the stream need not start at an aligned offset
            int offset = sink.Length;
            IntStreamWriter.Write(values, sink);
            byte[] file = [.. sink.Span, .. new byte[3]];
            IntStream stream = IntStream.Parse("t", file, offset, file.Length - 3);
            Assert.Equal(count, stream.Count);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(values[i], stream.Get(i));
            }

            int from = count == 0 ? 0 : rng.Next(count);
            long[] run = new long[count - from];
            stream.Decode(from, run);
            Assert.True(values.AsSpan(from).SequenceEqual(run));
            cases += count + 1;
        }

        Assert.True(cases > 100_000);
    }

    [Fact]
    public void LzRoundTripsAndRefusesDamage()
    {
        var rng = new Random(5);
        var lz = new LzCompressor();
        for (int round = 0; round < 300; round++)
        {
            byte[] data = new byte[rng.Next(0, 70_000)];
            int mode = rng.Next(4);
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = mode switch
                {
                    0 => (byte)rng.Next(256),
                    1 => (byte)(i % 7),
                    2 => (byte)"the quick brown fox "[i % 20],
                    _ => (byte)(rng.Next(10) == 0 ? rng.Next(256) : 'a'),
                };
            }

            var packed = new ByteBuilder();
            lz.Compress(data, packed);
            Assert.True(packed.Length <= LzCompressor.MaxCompressedLength(data.Length));
            var back = new byte[data.Length];
            Assert.True(LzDecompressor.TryDecompress(packed.Span, back));
            Assert.True(data.AsSpan().SequenceEqual(back));
            if (packed.Length > 3)
            {
                byte[] bad = packed.Span.ToArray();
                bad[rng.Next(bad.Length)] ^= (byte)(1 << rng.Next(8));
                _ = LzDecompressor.TryDecompress(bad, new byte[data.Length]); // must return, whatever it decides
                Assert.False(LzDecompressor.TryDecompress(packed.Span[..^1], new byte[data.Length]));
            }
        }
    }

    [Fact]
    public void ValuesSurviveFlushDeletesAndMerge()
    {
        int segmentsSeen = 0;
        int docsChecked = 0;
        for (int seed = 1; seed <= 16; seed++)
        {
            var storage = new MemoryStorage();
            var options = new IndexWriterOptions { MaxBufferedDocuments = 53, AutoMerge = false };
            var expected = new Dictionary<string, Document>();
            using (IndexWriter writer = IndexWriter.Open(storage, TestSchema, options))
            {
                foreach (Document d in DocValuesFixture.Generate(seed, 330))
                {
                    writer.Upsert(d);
                    expected[d.Get("id").AsText()] = d;
                }

                writer.Commit();
                using (IndexSnapshot s = IndexSnapshot.Open(storage))
                {
                    segmentsSeen += s.SegmentCount;
                    docsChecked += Verify(s, expected, seed);
                }

                var rng = new Random(seed);
                foreach (string id in expected.Keys.ToList())
                {
                    if (rng.Next(3) == 0)
                    {
                        Assert.True(writer.DeleteById(id));
                        expected.Remove(id);
                    }
                }

                // Replacements after the first commit: new values for existing ids, in a later segment.
                foreach (Document d in DocValuesFixture.Generate(seed + 1000, 40, first: 300))
                {
                    writer.Upsert(d);
                    expected[d.Get("id").AsText()] = d;
                }

                writer.Commit();
                using (IndexSnapshot s = IndexSnapshot.Open(storage))
                {
                    docsChecked += Verify(s, expected, seed);
                }

                writer.ForceMerge(1);
                writer.Commit();
            }

            using IndexSnapshot merged = IndexSnapshot.Open(storage);
            Assert.Equal(1, merged.SegmentCount);
            docsChecked += Verify(merged, expected, seed);
        }

        Assert.True(segmentsSeen >= 60);
        Assert.True(docsChecked > 10_000);
        TestContext.Current.SendDiagnosticMessage($"documents checked: {docsChecked}; segments read before merging: {segmentsSeen}; column and range cases: {s_cases}");
    }

    [Fact]
    public void MergingGivesTheBytesThatFlushingTheSameDocumentsGives()
    {
        foreach (int seed in new[] { 3, 4, 5 })
        {
            List<Document> all = DocValuesFixture.Generate(seed, 400);
            var survivors = all.Where((_, i) => i % 4 != 1).ToList();

            var merged = new MemoryStorage();
            using (IndexWriter w = IndexWriter.Open(merged, TestSchema, new IndexWriterOptions { MaxBufferedDocuments = 70, AutoMerge = false }))
            {
                foreach (Document d in all)
                {
                    w.Upsert(d);
                }

                w.Commit();
                for (int i = 1; i < all.Count; i += 4)
                {
                    w.DeleteById(all[i].Get("id").AsText());
                }

                w.Commit();
                w.ForceMerge(1);
                w.Commit();
            }

            var flushed = new MemoryStorage();
            using (IndexWriter w = IndexWriter.Open(flushed, TestSchema, new IndexWriterOptions { MaxBufferedDocuments = 10_000, AutoMerge = false }))
            {
                foreach (Document d in survivors)
                {
                    w.Upsert(d);
                }

                w.Commit();
            }

            using IndexSnapshot a = IndexSnapshot.Open(merged);
            using IndexSnapshot b = IndexSnapshot.Open(flushed);
            Assert.Equal(1, a.SegmentCount);
            Assert.Equal(1, b.SegmentCount);
            foreach (string part in new[] { SegmentParts.Columns, SegmentParts.Points, SegmentParts.Stored })
            {
                byte[] x = TestHelpers.ReadAll(merged, a.GetSegment(0).Info.FindPart(part)!.FileName);
                byte[] y = TestHelpers.ReadAll(flushed, b.GetSegment(0).Info.FindPart(part)!.FileName);
                Assert.True(x.AsSpan().SequenceEqual(y), $"part '{part}' differs between merge and flush for seed {seed}");
            }
        }
    }

    [Fact]
    public void StoredSourceIsByteIdenticalAndSchemaStoredFlagIsHonoured()
    {
        var storage = new MemoryStorage();
        var docs = DocValuesFixture.Generate(21, 300);
        using (IndexWriter w = IndexWriter.Open(storage, TestSchema, new IndexWriterOptions { MaxBufferedDocuments = 120, AutoMerge = false }))
        {
            foreach (Document d in docs)
            {
                w.Upsert(d);
            }

            w.Commit();
        }

        using IndexSnapshot snap = IndexSnapshot.Open(storage);
        int seen = 0;
        for (int s = 0; s < snap.SegmentCount; s++)
        {
            SegmentReader seg = snap.GetSegment(s);
            IStoredFieldsReader stored = seg.GetPart<IStoredFieldsSource>()!.StoredFields;
            IColumnReader ids = seg.GetPart<IColumnSource>()!.GetColumn("id")!;
            for (int d = 0; d < seg.MaxDoc; d++)
            {
                string id = Encoding.UTF8.GetString(ids.GetKeyword(d));
                Document expected = docs.Single(x => x.Get("id").AsText() == id);
                Document actual = stored.GetDocument(d);
                Assert.True(DocValuesFixture.SameStored(TestSchema, expected, actual), $"stored document {id} differs");
                Assert.DoesNotContain(actual.Fields, f => f.Name == "secret");
                var one = new ArrayBufferWriter<byte>();
                var two = new ArrayBufferWriter<byte>();
                Assert.True(stored.TryGetSource(d, one));
                Assert.True(stored.TryGetSource(d, two));
                Assert.True(one.WrittenSpan.SequenceEqual(two.WrittenSpan));
                seen++;
            }
        }

        Assert.Equal(300, seen);

        // A schema that stores nothing writes no stored part.
        var none = new MemoryStorage();
        Schema noStore = new SchemaBuilder().Add(new FieldDefinition("t", FieldType.Text) { Stored = false }).Build();
        using (IndexWriter w = IndexWriter.Open(none, noStore))
        {
            w.Add(new Document().Add("t", "hello"));
            w.Commit();
        }

        using IndexSnapshot ns = IndexSnapshot.Open(none);
        Assert.Null(ns.GetSegment(0).Info.FindPart(SegmentParts.Stored));
        Assert.Null(ns.GetSegment(0).GetPart<IStoredFieldsSource>());
    }

    [Fact]
    public void HotPathsAllocateNothing()
    {
        var storage = new MemoryStorage();
        using (IndexWriter w = IndexWriter.Open(storage, TestSchema, new IndexWriterOptions { MaxBufferedDocuments = 100_000 }))
        {
            foreach (Document d in DocValuesFixture.Generate(9, 3000))
            {
                w.Upsert(d);
            }

            w.Commit();
        }

        using IndexSnapshot snap = IndexSnapshot.Open(storage);
        SegmentReader seg = snap.GetSegment(0);
        var columns = seg.GetPart<IColumnSource>()!;
        var id = (SegmentColumn)columns.GetColumn("id")!;      // dense keyword
        var n = (SegmentColumn)columns.GetColumn("n")!;        // optional integer
        var f = (SegmentColumn)columns.GetColumn("f")!;        // optional double
        var nums = (SegmentColumn)columns.GetColumn("nums")!;  // multi-valued integer
        var tags = (SegmentColumn)columns.GetColumn("tags")!;  // multi-valued keyword
        var loc = (SegmentColumn)columns.GetColumn("loc")!;
        int max = seg.MaxDoc;
        long sink = 0;

        TestHelpers.AssertNoAllocation("column random access", () =>
        {
            for (int d = 0; d < max; d++)
            {
                sink += id.GetKeyword(d).Length;
                if (n.HasValue(d))
                {
                    sink += n.GetInt64(d);
                }

                if (f.HasValue(d))
                {
                    sink += (long)f.GetFloat64(d);
                }

                int c = nums.ValueCount(d);
                for (int i = 0; i < c; i++)
                {
                    sink += nums.GetInt64(d, i);
                }

                c = tags.ValueCount(d);
                for (int i = 0; i < c; i++)
                {
                    sink += tags.GetKeyword(d, i).Length + tags.GetOrdinal(d, i);
                }

                if (loc.HasValue(d))
                {
                    sink += (long)loc.GetGeoPoint(d).Latitude;
                }
            }
        });

        var scan = new long[max];
        long total = nums.TotalValues;
        var wide = new long[total];
        TestHelpers.AssertNoAllocation("column sequential scan", () =>
        {
            id.ReadValues(0, scan);
            nums.ReadValues(0, wide);
            for (int i = 0; i < scan.Length; i++)
            {
                sink += scan[i];
            }
        });

        var points = (SegmentPoints)seg.GetPart<IPointSource>()!.GetPoints("n")!;
        DocIdSetIterator wideRange = points.GetRange(long.MinValue, long.MaxValue);
        DocIdSetIterator narrow = points.GetRange(-3, 3);
        DocIdSetIterator[] iterators = [wideRange, narrow];
        TestHelpers.AssertNoAllocation("point range iteration", () =>
        {
            foreach (DocIdSetIterator it in iterators)
            {
                int d = it.Advance(0);
                while (d != DocIdSetIterator.NoMoreDocs)
                {
                    sink += d;
                    d = it.NextDoc();
                }
            }
        });

        Assert.NotEqual(long.MinValue, sink);
    }

    [Fact]
    public void DamagedFilesRaiseTypedExceptionsOnly()
    {
        var rng = new Random(77);
        var baseline = DocValuesFixture.BuildBaseline(7, 200);
        int mutations = 0;
        for (int which = 0; which < 3; which++)
        {
            byte[] good = which switch { 0 => baseline.Columns, 1 => baseline.Points, _ => baseline.Stored };
            DocValuesTarget.Exercise(good, which); // the undamaged file reads clean
            for (int i = 0; i < 1500; i++)
            {
                byte[] file = (byte[])good.Clone();
                switch (rng.Next(4))
                {
                    case 0:
                        file[rng.Next(file.Length)] ^= (byte)(1 << rng.Next(8));
                        break;
                    case 1:
                        file = file[..rng.Next(file.Length)];
                        break;
                    case 2:
                        for (int k = 0; k < 4; k++)
                        {
                            file[rng.Next(file.Length)] = (byte)rng.Next(256);
                        }

                        break;
                    default:
                        rng.NextBytes(file.AsSpan(rng.Next(file.Length), Math.Min(8, file.Length - 1)));
                        break;
                }

                // Repair the frame half of the time so the damage reaches the structure parsers instead of the checksum.
                if (i % 2 == 0 && file.Length >= 20)
                {
                    byte[] input = [1, .. file];
                    DocValuesTarget.Run(input, which);
                }
                else
                {
                    DocValuesTarget.Exercise(file, which);
                }

                mutations++;
            }
        }

        Assert.Equal(4500, mutations);
    }

    [Fact]
    public void RangeQueriesAgreeBetweenThePointIndexAndNumericTerms()
    {
        List<Document> docs = DocValuesFixture.Generate(41, 700);
        (IndexSnapshot withPoints, IndexSearcher a) = BuildSearcher(docs, SegmentCodecs.CreateDefault());
        (IndexSnapshot termsOnly, IndexSearcher b) = BuildSearcher(docs, [new TextSegmentCodec(), new ColumnSegmentCodec(), new StoredSegmentCodec()]);
        using (withPoints)
        using (termsOnly)
        {
            Assert.NotNull(withPoints.GetSegment(0).GetPart<IPointSource>()!.GetPoints("n"));
            Assert.Null(termsOnly.GetSegment(0).GetPart<IPointSource>());
            var rng = new Random(8);
            int queries = 0;
            foreach (string field in new[] { "n", "f", "when", "flag", "nums" })
            {
                FieldType type = TestSchema.TryGetField(field, out FieldDefinition? def) ? def.Type : throw new InvalidOperationException(field);
                var values = docs.SelectMany(d => d.Fields.Where(f => f.Name == field).Select(f => f.Value)).Where(v => type != FieldType.Fractional || !double.IsNaN(v.AsFloat64())).ToList();
                for (int i = 0; i < 60; i++)
                {
                    FieldValue? Bound() => rng.Next(5) == 0 ? null : values[rng.Next(values.Count)];
                    var q = new RangeQuery(field, Bound(), Bound()) { IncludeLower = rng.Next(2) == 0, IncludeUpper = rng.Next(2) == 0 };
                    TopDocs x;
                    TopDocs y;
                    try
                    {
                        x = a.Search(q, 1000, countExactly: true);
                    }
                    catch (QueryException)
                    {
                        Assert.Throws<QueryException>(() => b.Search(q, 1000, countExactly: true));
                        continue;
                    }

                    y = b.Search(q, 1000, countExactly: true);
                    Assert.Equal(y.TotalHits, x.TotalHits);
                    Assert.Equal(y.Hits.Select(h => h.DocId).Order(), x.Hits.Select(h => h.DocId).Order());
                    queries++;
                }
            }

            Assert.True(queries > 200);
        }
    }

    private static (IndexSnapshot, IndexSearcher) BuildSearcher(List<Document> docs, IReadOnlyList<ISegmentCodec> codecs)
    {
        var storage = new MemoryStorage();
        using (IndexWriter w = IndexWriter.Open(storage, TestSchema, new IndexWriterOptions { Codecs = codecs, MaxBufferedDocuments = 10_000, AutoMerge = false }))
        {
            foreach (Document d in docs)
            {
                w.Upsert(d);
            }

            w.Commit();
        }

        IndexSnapshot snapshot = IndexSnapshot.Open(storage, codecs);
        return (snapshot, SnapshotSearch.CreateSearcher(snapshot));
    }

    // ---- verification ------------------------------------------------------------------------------------------

    private static int Verify(IndexSnapshot snapshot, Dictionary<string, Document> expected, int seed)
    {
        int live = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int s = 0; s < snapshot.SegmentCount; s++)
        {
            SegmentReader seg = snapshot.GetSegment(s);
            IColumnSource columns = seg.GetPart<IColumnSource>()!;
            IStoredFieldsReader stored = seg.GetPart<IStoredFieldsSource>()!.StoredFields;
            IColumnReader ids = columns.GetColumn("id")!;
            for (int d = 0; d < seg.MaxDoc; d++)
            {
                if (seg.Live.IsDeleted(d))
                {
                    continue;
                }

                string id = Encoding.UTF8.GetString(ids.GetKeyword(d));
                Assert.True(seen.Add(id), $"seed {seed}: {id} is live twice");
                Document exp = expected[id];
                CompareColumns(columns, d, exp, id);
                Assert.True(DocValuesFixture.SameStored(TestSchema, exp, stored.GetDocument(d)), $"seed {seed}: stored {id} differs");
                live++;
            }
        }

        Assert.Equal(expected.Count, live);
        CheckPointRanges(snapshot, expected, seed);
        return live;
    }

    private static void CompareColumns(IColumnSource columns, int doc, Document exp, string id)
    {
        foreach (string field in DocValuesFixture.ColumnFieldNames.Append("raw"))
        {
            FieldType type = TestSchema.TryGetField(field, out FieldDefinition? def) ? def.Type : throw new InvalidOperationException(field);
            List<FieldValue> want = [.. exp.Fields.Where(f => f.Name == field).Select(f => f.Value)];
            IColumnReader? column = columns.GetColumn(field);
            if (column is null)
            {
                Assert.Empty(want);
                continue;
            }

            Assert.Equal(want.Count, column.ValueCount(doc));
            Assert.Equal(want.Count > 0, column.HasValue(doc));
            for (int i = 0; i < want.Count; i++)
            {
                switch (type)
                {
                    case FieldType.Keyword:
                        Assert.True(column.GetKeyword(doc, i).SequenceEqual(Encoding.UTF8.GetBytes(want[i].AsText())), $"{id}.{field}[{i}]");
                        break;
                    case FieldType.Fractional:
                        Assert.Equal(BitConverter.DoubleToInt64Bits(want[i].AsFloat64()), BitConverter.DoubleToInt64Bits(column.GetFloat64(doc, i)));
                        break;
                    case FieldType.GeoPoint:
                        Assert.Equal(want[i].AsGeoPoint(), column.GetGeoPoint(doc, i));
                        break;
                    case FieldType.Bool:
                        Assert.Equal(want[i].AsBool() ? 1 : 0, column.GetInt64(doc, i));
                        break;
                    default:
                        Assert.Equal(want[i].AsInt64(), column.GetInt64(doc, i));
                        break;
                }
            }

            s_cases++;
        }
    }

    private static void CheckPointRanges(IndexSnapshot snapshot, Dictionary<string, Document> expected, int seed)
    {
        var rng = new Random(seed * 31);
        foreach (string field in DocValuesFixture.PointFieldNames)
        {
            FieldType type = TestSchema.TryGetField(field, out FieldDefinition? def) ? def.Type : throw new InvalidOperationException(field);
            var ordered = new Dictionary<string, List<long>>();
            var pool = new List<long>();
            foreach ((string id, Document d) in expected)
            {
                List<long> v = [.. d.Fields.Where(f => f.Name == field).Select(f => NumericTerms.Ordered(type, f.Value))];
                ordered[id] = v;
                pool.AddRange(v);
            }

            for (int round = 0; round < 12; round++)
            {
                long Pick() => rng.Next(6) switch
                {
                    0 => long.MinValue,
                    1 => long.MaxValue,
                    _ => pool.Count == 0 ? 0 : pool[rng.Next(pool.Count)] + rng.Next(-1, 2),
                };

                long lo = Pick();
                long hi = rng.Next(5) == 0 ? lo : Pick();
                if (rng.Next(3) == 0 && lo != long.MaxValue)
                {
                    lo++; // an exclusive lower bound, as the query layer turns it into an inclusive one
                }

                if (rng.Next(3) == 0 && hi != long.MinValue)
                {
                    hi--;
                }

                var want = new HashSet<string>(ordered.Where(kv => kv.Value.Any(x => x >= lo && x <= hi)).Select(kv => kv.Key), StringComparer.Ordinal);
                var got = new HashSet<string>(StringComparer.Ordinal);
                for (int s = 0; s < snapshot.SegmentCount; s++)
                {
                    SegmentReader seg = snapshot.GetSegment(s);
                    IColumnReader ids = seg.GetPart<IColumnSource>()!.GetColumn("id")!;
                    IPointIndexReader? points = seg.GetPart<IPointSource>()!.GetPoints(field);
                    if (points is null)
                    {
                        continue;
                    }

                    DocIdSetIterator it = points.GetRange(lo, hi);
                    int previous = -1;
                    for (int d = it.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = it.NextDoc())
                    {
                        Assert.True(d > previous, "documents must increase");
                        previous = d;
                        if (!seg.Live.IsDeleted(d))
                        {
                            Assert.True(got.Add(Encoding.UTF8.GetString(ids.GetKeyword(d))));
                        }
                    }

                    // Advance must land on the same documents as stepping does.
                    var all = new List<int>();
                    it = points.GetRange(lo, hi);
                    for (int d = it.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = it.NextDoc())
                    {
                        all.Add(d);
                    }

                    int step = rng.Next(1, 40);
                    var viaAdvance = new List<int>();
                    it = points.GetRange(lo, hi);
                    for (int d = it.Advance(0); d != DocIdSetIterator.NoMoreDocs; d = it.Advance(d + step))
                    {
                        viaAdvance.Add(d);
                    }

                    var byScan = new List<int>();
                    int cursor = all.Count > 0 ? all[0] : -1;
                    while (cursor >= 0)
                    {
                        byScan.Add(cursor);
                        int target = cursor + step;
                        cursor = all.FirstOrDefault(x => x >= target, -1);
                    }

                    Assert.Equal(byScan, viaAdvance);
                }

                Assert.True(want.SetEquals(got), $"seed {seed}, field {field}, range [{lo}, {hi}]: {want.Count} expected, {got.Count} found");
                s_cases++;
            }
        }
    }
}
