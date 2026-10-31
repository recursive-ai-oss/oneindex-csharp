using System.Globalization;
using OneIndex.Aggregations;
using OneIndex.Search;
using Row = OneIndex.Tests.AggCorpus.Row;

namespace OneIndex.Tests;

/// <summary>Facets, aggregations and distinct against a LINQ-to-Objects reference over seeded random corpora.</summary>
public sealed class AggregationDifferentialTests(ITestOutputHelper output)
{
    private static readonly DateTime Epoch = DateTime.UnixEpoch;

    private static (Query Query, Func<Row, bool> Match) RandomQuery(Random r)
    {
        string w1 = AggCorpus.Words[r.Next(AggCorpus.Words.Length)];
        string w2 = AggCorpus.Words[r.Next(AggCorpus.Words.Length)];
        switch (r.Next(5))
        {
            case 0:
                return (new MatchAllQuery(), _ => true);
            case 1:
                return (new TermQuery("body", w1), x => x.Body.Contains(w1));
            case 2:
                return (BooleanQuery.Of(new BooleanClause(new TermQuery("body", w1), Occur.Must), new BooleanClause(new TermQuery("body", w2), Occur.MustNot)),
                    x => x.Body.Contains(w1) && !x.Body.Contains(w2));
            case 3:
            {
                string t = AggCorpus.TagPool[r.Next(AggCorpus.TagPool.Length)];
                return (new TermQuery("tags", t), x => x.Tags.Contains(t));
            }

            default:
            {
                string c = AggCorpus.Cats[r.Next(AggCorpus.Cats.Length)];
                return (BooleanQuery.Of(new BooleanClause(new TermQuery("cat", c), Occur.Should), new BooleanClause(new TermQuery("body", w1), Occur.Should)),
                    x => x.Cat == c || x.Body.Contains(w1));
            }
        }
    }

    // ---- reference helpers ----

    private static List<(TKey Key, long Count)> RefTerms<TKey>(IEnumerable<IEnumerable<TKey>> perDocument, Comparison<TKey> compare, TermsOrder order, int size, long minDocCount, out long other)
        where TKey : notnull
    {
        var counts = new Dictionary<TKey, long>();
        foreach (IEnumerable<TKey> values in perDocument)
        {
            foreach (TKey k in values.Distinct())
            {
                counts[k] = counts.GetValueOrDefault(k) + 1;
            }
        }

        var list = counts.Where(kv => kv.Value >= minDocCount).Select(kv => (kv.Key, kv.Value)).ToList();
        list.Sort((a, b) =>
        {
            int kc = compare(a.Key, b.Key);
            return order switch
            {
                TermsOrder.KeyAscending => kc,
                TermsOrder.KeyDescending => -kc,
                _ => a.Value != b.Value ? b.Value.CompareTo(a.Value) : kc,
            };
        });
        other = list.Skip(size).Sum(x => x.Value);
        return [.. list.Take(size)];
    }

    private static void AssertTerms<TKey>(BucketsResult actual, List<(TKey Key, long Count)> expected, long expectedOther, Func<TKey, string> text, string what)
    {
        Assert.True(expected.Count == actual.Buckets.Count, $"{what}: {actual.Buckets.Count} buckets, expected {expected.Count}");
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(text(expected[i].Key) == actual.Buckets[i].Key.Text && expected[i].Count == actual.Buckets[i].DocCount,
                $"{what}: bucket {i} is {actual.Buckets[i].Key.Text}={actual.Buckets[i].DocCount}, expected {text(expected[i].Key)}={expected[i].Count}");
        }

        Assert.True(expectedOther == actual.OtherDocCount, $"{what}: other {actual.OtherDocCount}, expected {expectedOther}");
    }

    private static string D(double d) => (d == 0 ? 0.0 : d).ToString("R", CultureInfo.InvariantCulture);

    private static void AssertStats(StatsResult s, IReadOnlyList<double> values, bool exactSum, string what)
    {
        Assert.True(values.Count == s.Count, $"{what}: count {s.Count}, expected {values.Count}");
        if (values.Count == 0)
        {
            Assert.True(s.Min is null && s.Max is null && s.Avg is null && s.Sum == 0, $"{what}: empty stats");
            return;
        }

        Assert.True(values.Min() == s.Min && values.Max() == s.Max, $"{what}: min/max {s.Min}/{s.Max}, expected {values.Min()}/{values.Max()}");
        double sum = values.Sum();
        double tolerance = exactSum ? 0 : 1e-9 * Math.Max(1, values.Sum(Math.Abs));
        Assert.True(Math.Abs(sum - s.Sum) <= tolerance, $"{what}: sum {s.Sum:R}, expected {sum:R}");
    }

    private static IEnumerable<double> Prices(IEnumerable<Row> rows) => rows.Where(x => x.Price is not null).Select(x => x.Price!.Value);

    private static IEnumerable<double> Ns(IEnumerable<Row> rows) => rows.Where(x => x.N is not null).Select(x => (double)x.N!.Value);

    private static void AssertPercentiles(PercentilesResult p, List<double> values, string what)
    {
        values.Sort();
        foreach (KeyValuePair<double, double> kv in p.Values)
        {
            if (values.Count == 0)
            {
                Assert.True(double.IsNaN(kv.Value), what);
                continue;
            }

            double eps = 0.02 + (2.0 / values.Count);
            double q = kv.Key / 100.0;
            int below = values.Count(v => v < kv.Value);
            int atOrBelow = values.Count(v => v <= kv.Value);
            Assert.True(below <= Math.Ceiling((q + eps) * values.Count) && atOrBelow >= Math.Floor((q - eps) * values.Count),
                $"{what}: p{kv.Key} = {kv.Value} has rank {below}..{atOrBelow} of {values.Count}");
            Assert.True(kv.Value >= values[0] && kv.Value <= values[^1], what);
        }
    }

    private static long FloorMonthStart(long ms, DateInterval unit)
    {
        DateTime dt = Epoch.AddMilliseconds(ms);
        DateTime start = unit switch
        {
            DateInterval.Month => new DateTime(dt.Year, dt.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            DateInterval.Quarter => new DateTime(dt.Year, ((dt.Month - 1) / 3 * 3) + 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DateInterval.Year => new DateTime(dt.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DateInterval.Week => dt.Date.AddDays(-(((int)dt.DayOfWeek + 6) % 7)),
            DateInterval.Day => dt.Date,
            DateInterval.Hour => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, DateTimeKind.Utc),
            _ => throw new ArgumentOutOfRangeException(nameof(unit)),
        };
        return (long)(DateTime.SpecifyKind(start, DateTimeKind.Utc) - Epoch).TotalMilliseconds;
    }

    private static long NextStart(long start, DateInterval unit)
    {
        DateTime dt = Epoch.AddMilliseconds(start);
        DateTime next = unit switch
        {
            DateInterval.Month => dt.AddMonths(1),
            DateInterval.Quarter => dt.AddMonths(3),
            DateInterval.Year => dt.AddYears(1),
            DateInterval.Week => dt.AddDays(7),
            DateInterval.Day => dt.AddDays(1),
            _ => dt.AddHours(1),
        };
        return (long)(next - Epoch).TotalMilliseconds;
    }

    // ---- the differential ----

    [Fact]
    public void EveryFacetStatAggregationBucketAndDistinctPageEqualsLinqToObjects()
    {
        int cases = int.TryParse(Environment.GetEnvironmentVariable("ONEINDEX_AGG_CASES"), out int n) ? n : 150;
        long queries = 0;
        long checks = 0;
        int multiSegment = 0;
        int merged = 0;
        for (int seed = 0; seed < cases; seed++)
        {
            using AggCorpus corpus = AggCorpus.Build(seed);
            multiSegment += corpus.Segments > 1 ? 1 : 0;
            merged += corpus.Merged ? 1 : 0;
            var r = new Random(seed * 7919);
            for (int q = 0; q < 5; q++)
            {
                (Query query, Func<Row, bool> match) = RandomQuery(r);
                List<Row> hits = [.. corpus.Live.Where(match)];
                queries++;
                checks += CheckFacets(corpus, query, hits, r);
                checks += CheckAggregations(corpus, query, hits, r);
                checks += CheckDistinct(corpus, query, hits, r);
            }
        }

        output.WriteLine($"{cases} corpora ({multiSegment} with several segments, {merged} merged), {queries} queries, {checks} compared results");
        Assert.True(multiSegment > cases / 4 && merged > cases / 8);
    }

    private static int CheckFacets(AggCorpus corpus, Query query, List<Row> hits, Random r)
    {
        var requests = new List<FacetRequest>
        {
            new("cat") { SortBy = r.Next(2) == 0 ? FacetSort.Alpha : FacetSort.Count, MaxValues = r.Next(1, 9) },
            new("tags") { SortBy = r.Next(2) == 0 ? FacetSort.Alpha : FacetSort.Count, MaxValues = r.Next(1, 20) },
            new("n") { SortBy = r.Next(2) == 0 ? FacetSort.Alpha : FacetSort.Count },
            new("price") { SortBy = FacetSort.Count },
            new("flag"),
            new("when") { MaxValues = 5 },
            new("ns") { SortBy = FacetSort.Count, MaxValues = 7 },
        };
        var collector = new FacetCollector(AggCorpus.Schema, requests);
        corpus.Searcher.Search(query, collector);
        IReadOnlyList<FacetResult> results = collector.GetResults();

        void Check<TKey>(int index, IEnumerable<IEnumerable<TKey>> values, Comparison<TKey> compare, Func<TKey, string> text)
            where TKey : notnull
        {
            FacetRequest req = requests[index];
            // Reference: all distinct values with counts, sorted, then cut.
            var all = RefTerms(values, compare, TermsOrder.KeyAscending, int.MaxValue, 1, out _);
            if (req.SortBy == FacetSort.Count)
            {
                all = [.. all.OrderByDescending(x => x.Count)]; // stable: ties keep the ascending value order
            }

            FacetResult actual = results[index];
            Assert.Equal(all.Count, actual.DistinctValues);
            var expected = all.Take(req.MaxValues).Select(x => (text(x.Key), x.Count)).ToList();
            var got = actual.Values.Select(v => (v.Value, v.Count)).ToList();
            Assert.True(expected.SequenceEqual(got), $"facet {req.Field}: [{string.Join(",", got)}] expected [{string.Join(",", expected)}]");
        }

        Check(0, hits.Where(x => x.Cat is not null).Select(x => new[] { x.Cat! }), string.CompareOrdinal, s => s);
        Check(1, hits.Select(x => x.Tags), string.CompareOrdinal, s => s);
        Check(2, hits.Where(x => x.N is not null).Select(x => new[] { x.N!.Value }), (a, b) => a.CompareTo(b), v => v.ToString(CultureInfo.InvariantCulture));
        Check(3, hits.Where(x => x.Price is not null).Select(x => new[] { x.Price!.Value == 0 ? 0.0 : x.Price.Value }), (a, b) => a.CompareTo(b), D);
        Check(4, hits.Where(x => x.Flag is not null).Select(x => new[] { x.Flag!.Value }), (a, b) => a.CompareTo(b), v => v ? "true" : "false");
        Check(5, hits.Where(x => x.When is not null).Select(x => new[] { x.When!.Value }), (a, b) => a.CompareTo(b), v => Epoch.AddMilliseconds(v).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        Check(6, hits.Select(x => x.Ns), (a, b) => a.CompareTo(b), v => v.ToString(CultureInfo.InvariantCulture));

        // Stats of numeric facets are the min and max of the values.
        double[] ns = [.. hits.Where(x => x.N is not null).Select(x => (double)x.N!.Value)];
        Assert.Equal(ns.Length == 0 ? (FacetStats?)null : new FacetStats(ns.Min(), ns.Max()), results[2].Stats);
        double[] prices = [.. Prices(hits)];
        Assert.Equal(prices.Length == 0 ? (FacetStats?)null : new FacetStats(prices.Min(), prices.Max()), results[3].Stats);
        Assert.Null(results[0].Stats);
        return requests.Count;
    }

    private static int CheckAggregations(AggCorpus corpus, Query query, List<Row> hits, Random r)
    {
        var order = (TermsOrder)r.Next(3);
        int size = r.Next(1, 12);
        long minDoc = r.Next(1, 3);
        double interval = new[] { 1.0, 3.0, 7.5, 10.0 }[r.Next(4)];
        double offset = r.Next(0, 3) * 0.5;
        var calendar = new[] { DateInterval.Month, DateInterval.Quarter, DateInterval.Year, DateInterval.Week, DateInterval.Day }[r.Next(5)];
        var ranges = new List<AggregationRange> { new(null, 0), new(-5, 20), new(10, 10), new(15, null, "high"), new(null, null) };
        var aggs = new List<Aggregation>
        {
            new TermsAggregation("cat", "cat") { Size = size, Order = order, MinDocCount = minDoc, SubAggregations = [new StatsAggregation("p", "price"), new TermsAggregation("f", "flag"), new CardinalityAggregation("t", "tags")] },
            new TermsAggregation("tags", "tags") { Size = size, Order = order, MinDocCount = minDoc },
            new TermsAggregation("n", "n") { Size = size, Order = order, MinDocCount = minDoc },
            new TermsAggregation("price", "price") { Size = size, Order = order },
            new RangeAggregation("range", "n", ranges) { SubAggregations = [new StatsAggregation("s", "ns")] },
            new RangeAggregation("prange", "price", [new AggregationRange(0, 5), new AggregationRange(-2, 100)]),
            new HistogramAggregation("hist", "n", interval) { Offset = offset, MinDocCount = r.Next(3), SubAggregations = [new StatsAggregation("p", "price")] },
            new HistogramAggregation("phist", "price", 2.5) { MinDocCount = 1 },
            new DateHistogramAggregation("dh", "when") { CalendarInterval = calendar, MinDocCount = r.Next(2), SubAggregations = [new TermsAggregation("c", "cat") { Size = 2 }] },
            new DateHistogramAggregation("dfix", "when") { FixedInterval = TimeSpan.FromDays(400), Offset = TimeSpan.FromHours(5), MinDocCount = 1 },
            new StatsAggregation("sn", "n"),
            new StatsAggregation("sns", "ns"),
            new StatsAggregation("sp", "price"),
            new StatsAggregation("sw", "when"),
            new CardinalityAggregation("ct", "tags"),
            new CardinalityAggregation("cc", "cat"),
            new CardinalityAggregation("cn", "ns"),
            new CardinalityAggregation("cp", "price"),
            new PercentilesAggregation("pn", "n"),
            new PercentilesAggregation("pns", "ns") { Percents = [0, 50, 100] },
        };
        var collector = new AggregationCollector(AggCorpus.Schema, aggs);
        corpus.Searcher.Search(query, collector);
        AggregationResultSet res = collector.GetResults();
        Assert.Equal(aggs.Count, res.Count);

        // terms
        AssertTerms(res.Get<BucketsResult>("cat"), RefTerms(hits.Where(x => x.Cat is not null).Select(x => new[] { x.Cat! }), string.CompareOrdinal, order, size, minDoc, out long o1), o1, s => s, "terms cat");
        AssertTerms(res.Get<BucketsResult>("tags"), RefTerms(hits.Select(x => x.Tags), string.CompareOrdinal, order, size, minDoc, out long o2), o2, s => s, "terms tags");
        AssertTerms(res.Get<BucketsResult>("n"), RefTerms(hits.Where(x => x.N is not null).Select(x => new[] { x.N!.Value }), (a, b) => a.CompareTo(b), order, size, minDoc, out long o3), o3, v => v.ToString(CultureInfo.InvariantCulture), "terms n");
        AssertTerms(res.Get<BucketsResult>("price"), RefTerms(hits.Where(x => x.Price is not null).Select(x => new[] { x.Price!.Value == 0 ? 0.0 : x.Price.Value }), (a, b) => a.CompareTo(b), order, size, 1, out long o4), o4, D, "terms price");

        // sub-aggregations inside terms buckets
        foreach (Bucket b in res.Get<BucketsResult>("cat").Buckets)
        {
            List<Row> inBucket = [.. hits.Where(x => x.Cat == b.Key.Text)];
            AssertStats(b.Aggregations.Get<StatsResult>("p"), [.. Prices(inBucket)], false, "cat>price stats");
            AssertTerms(b.Aggregations.Get<BucketsResult>("f"), RefTerms(inBucket.Where(x => x.Flag is not null).Select(x => new[] { x.Flag!.Value }), (a, c) => a.CompareTo(c), TermsOrder.CountDescending, 10, 1, out long of), of, v => v ? "true" : "false", "cat>flag");
            Assert.Equal(inBucket.SelectMany(x => x.Tags).Distinct().Count(), b.Aggregations.Get<CardinalityResult>("t").Value);
        }

        // ranges: from inclusive, to exclusive; a document counts once per range
        BucketsResult rr = res.Get<BucketsResult>("range");
        Assert.Equal(ranges.Count, rr.Buckets.Count);
        for (int i = 0; i < ranges.Count; i++)
        {
            AggregationRange rg = ranges[i];
            bool In(double v) => (rg.From is null || v >= rg.From) && (rg.To is null || v < rg.To);
            List<Row> inRange = [.. hits.Where(x => x.N is { } nv && In(nv))];
            Assert.True(inRange.Count == rr.Buckets[i].DocCount, $"range {i}: {rr.Buckets[i].DocCount}, expected {inRange.Count}");
            AssertStats(rr.Buckets[i].Aggregations.Get<StatsResult>("s"), [.. inRange.SelectMany(x => x.Ns).Select(v => (double)v)], true, "range>ns stats");
        }

        Assert.Equal("*-0", rr.Buckets[0].Key.Text);
        Assert.Equal("high", rr.Buckets[3].Key.Text);
        Assert.Equal("*-*", rr.Buckets[4].Key.Text);
        BucketsResult pr = res.Get<BucketsResult>("prange");
        Assert.Equal(Prices(hits).Count(v => v >= 0 && v < 5), pr.Buckets[0].DocCount);
        Assert.Equal(Prices(hits).Count(v => v >= -2 && v < 100), pr.Buckets[1].DocCount);

        // histogram on n
        long histMin = ((HistogramAggregation)aggs[6]).MinDocCount;
        var byBucket = new SortedDictionary<long, List<Row>>();
        foreach (Row x in hits.Where(x => x.N is not null))
        {
            long id = (long)Math.Floor((x.N!.Value - offset) / interval);
            if (!byBucket.TryGetValue(id, out List<Row>? list))
            {
                byBucket[id] = list = [];
            }

            list.Add(x);
        }

        var expectedHist = new List<(long Id, List<Row> Rows)>();
        if (byBucket.Count > 0)
        {
            for (long id = byBucket.Keys.First(); id <= byBucket.Keys.Last(); id++)
            {
                List<Row> rowsIn = byBucket.GetValueOrDefault(id) ?? [];
                if (rowsIn.Count >= histMin)
                {
                    expectedHist.Add((id, rowsIn));
                }
            }
        }

        BucketsResult hb = res.Get<BucketsResult>("hist");
        Assert.True(expectedHist.Count == hb.Buckets.Count, $"histogram buckets {hb.Buckets.Count}, expected {expectedHist.Count}");
        for (int i = 0; i < expectedHist.Count; i++)
        {
            Assert.Equal((expectedHist[i].Id * interval) + offset, hb.Buckets[i].Key.Number);
            Assert.Equal(expectedHist[i].Rows.Count, hb.Buckets[i].DocCount);
            AssertStats(hb.Buckets[i].Aggregations.Get<StatsResult>("p"), [.. Prices(expectedHist[i].Rows)], false, "hist>price");
        }

        BucketsResult ph = res.Get<BucketsResult>("phist");
        var expectedPh = Prices(hits).GroupBy(v => Math.Floor(v / 2.5)).OrderBy(g => g.Key).Select(g => (g.Key * 2.5, (long)hits.Count(x => x.Price is { } p && Math.Floor(p / 2.5) == g.Key))).ToList();
        Assert.Equal(expectedPh.Select(x => x.Item1), ph.Buckets.Select(b => b.Key.Number));
        Assert.Equal(expectedPh.Select(x => x.Item2), ph.Buckets.Select(b => b.DocCount));

        // date histogram, calendar
        var whens = hits.Where(x => x.When is not null).ToList();
        var dhGroups = whens.GroupBy(x => FloorMonthStart(x.When!.Value, calendar)).ToDictionary(g => g.Key, g => g.ToList());
        var expectedDh = new List<(long Start, List<Row> Rows)>();
        if (dhGroups.Count > 0)
        {
            long minDh = ((DateHistogramAggregation)aggs[8]).MinDocCount;
            for (long s = dhGroups.Keys.Min(); s <= dhGroups.Keys.Max(); s = NextStart(s, calendar))
            {
                List<Row> rowsIn = dhGroups.GetValueOrDefault(s) ?? [];
                if (rowsIn.Count >= minDh)
                {
                    expectedDh.Add((s, rowsIn));
                }
            }
        }

        BucketsResult dh = res.Get<BucketsResult>("dh");
        Assert.True(expectedDh.Count == dh.Buckets.Count, $"date histogram ({calendar}): {dh.Buckets.Count} buckets, expected {expectedDh.Count}");
        for (int i = 0; i < expectedDh.Count; i++)
        {
            Assert.Equal(expectedDh[i].Start, dh.Buckets[i].Key.Whole);
            Assert.Equal(expectedDh[i].Rows.Count, dh.Buckets[i].DocCount);
            AssertTerms(dh.Buckets[i].Aggregations.Get<BucketsResult>("c"), RefTerms(expectedDh[i].Rows.Where(x => x.Cat is not null).Select(x => new[] { x.Cat! }), string.CompareOrdinal, TermsOrder.CountDescending, 2, 1, out long oc), oc, s => s, "dh>cat");
        }

        // date histogram, fixed length with offset
        long len = (long)TimeSpan.FromDays(400).TotalMilliseconds;
        long off = (long)TimeSpan.FromHours(5).TotalMilliseconds;
        var fixedGroups = whens.GroupBy(x => (long)Math.Floor((x.When!.Value - off) / (double)len)).OrderBy(g => g.Key).ToList();
        BucketsResult df = res.Get<BucketsResult>("dfix");
        Assert.Equal(fixedGroups.Select(g => (g.Key * len) + off), df.Buckets.Select(b => b.Key.Whole));
        Assert.Equal(fixedGroups.Select(g => (long)g.Count()), df.Buckets.Select(b => b.DocCount));

        // stats
        AssertStats(res.Get<StatsResult>("sn"), [.. Ns(hits)], true, "stats n");
        AssertStats(res.Get<StatsResult>("sns"), [.. hits.SelectMany(x => x.Ns).Select(v => (double)v)], true, "stats ns");
        AssertStats(res.Get<StatsResult>("sp"), [.. Prices(hits)], false, "stats price");
        AssertStats(res.Get<StatsResult>("sw"), [.. whens.Select(x => (double)x.When!.Value)], true, "stats when");

        // cardinality is exact at these sizes (sparse sketch)
        Assert.Equal(hits.SelectMany(x => x.Tags).Distinct().Count(), res.Get<CardinalityResult>("ct").Value);
        Assert.Equal(hits.Where(x => x.Cat is not null).Select(x => x.Cat).Distinct().Count(), res.Get<CardinalityResult>("cc").Value);
        Assert.Equal(hits.SelectMany(x => x.Ns).Distinct().Count(), res.Get<CardinalityResult>("cn").Value);
        Assert.Equal(Prices(hits).Select(v => v == 0 ? 0.0 : v).Distinct().Count(), res.Get<CardinalityResult>("cp").Value);

        AssertPercentiles(res.Get<PercentilesResult>("pn"), [.. Ns(hits)], "percentiles n");
        AssertPercentiles(res.Get<PercentilesResult>("pns"), [.. hits.SelectMany(x => x.Ns).Select(v => (double)v)], "percentiles ns");
        return aggs.Count;
    }

    private static int CheckDistinct(AggCorpus corpus, Query query, List<Row> hits, Random r)
    {
        // Scores come from the engine (every match, exact); the grouping is recomputed here from the documents.
        TopDocs all = corpus.Searcher.Search(query, Math.Max(1, corpus.Live.Count + 1));
        Assert.Equal(hits.Count, all.Hits.Count);
        var score = all.Hits.ToDictionary(h => h.DocId, h => h.Score);
        string KeyOf(Row x) => x.Cat is null ? "\0none:" + x.Id : x.Cat;
        var groups = hits.GroupBy(KeyOf).Select(g =>
        {
            Row best = g.OrderByDescending(x => score[corpus.GlobalIds[x.Id]]).ThenBy(x => corpus.GlobalIds[x.Id]).First();
            return (Doc: corpus.GlobalIds[best.Id], Score: score[corpus.GlobalIds[best.Id]]);
        }).OrderByDescending(g => g.Score).ThenBy(g => g.Doc).ToList();

        int checks = 0;
        for (int page = 0; page < 3; page++)
        {
            int take = r.Next(1, 6);
            int skip = page * take;
            var collector = new DistinctCollector("cat", skip + take);
            corpus.Searcher.Search(query, collector);
            DistinctDocs got = collector.GetTopDocs();
            Assert.Equal(groups.Count, got.TotalGroups);
            Assert.Equal(hits.Count, got.TotalHits);
            var expectedPage = groups.Skip(skip).Take(take).Select(g => g.Doc).ToList();
            var gotPage = got.Hits.Skip(skip).Take(take).Select(h => h.DocId).ToList();
            Assert.True(expectedPage.SequenceEqual(gotPage), $"distinct page {page}: [{string.Join(",", gotPage)}] expected [{string.Join(",", expectedPage)}]");
            checks++;
        }

        // the numeric path: collapse by the flag (three groups at most, plus one per document without a value)
        var flagCollector = new DistinctCollector("flag", 1000);
        corpus.Searcher.Search(query, flagCollector);
        long expectedFlagGroups = hits.Where(x => x.Flag is not null).Select(x => x.Flag).Distinct().Count() + hits.Count(x => x.Flag is null);
        Assert.Equal(expectedFlagGroups, flagCollector.GetTopDocs().TotalGroups);
        return checks + 1;
    }
}
