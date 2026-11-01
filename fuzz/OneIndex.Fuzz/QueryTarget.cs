using System.Text;
using System.Text.RegularExpressions;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Fuzz;

/// <summary>
/// Query execution against a brute-force reference. The input bytes decide everything: a small random corpus (several segments, deletes,
/// optionally merged), a random query tree of every kind, and then the engine's answer is compared with a straightforward evaluator:
/// the same hit set, scores within a small relative tolerance of an exhaustive BM25F computed in double precision from the documents
/// themselves, the top-k pages bit-identical to the exhaustive list, exact totals, and explanations equal to the scores. Only the
/// documented typed errors may come out of the engine, and only for the queries that are meant to be invalid.
/// </summary>
internal static class QueryTarget
{
    private static readonly string[] Words = ["alpha", "beta", "gamma", "delta", "run", "runs", "runner", "running", "rung", "apple", "apply", "ample", "zeta"];
    private static readonly string[] Tags = ["red", "green", "blue", "reddish"];
    private static readonly string[] RegexPatterns = ["ru.*", "r[aeiou]n", "(run|rung)s?", "a.p.e", "[a-d].*a", "ap+le?", "z.*"];
    private static readonly double[] Doubles = [-1.5, 0.0, -0.0, 0.5, 2, 7.25, 40];

    public static void Run(ReadOnlySpan<byte> data) => RunCase(data.ToArray());

    /// <summary>Runs the case a seed stands for.</summary>
    public static CaseStats RunSeed(int seed)
    {
        var bytes = new byte[1 << 14];
        new Random(seed).NextBytes(bytes);
        return RunCase(bytes);
    }

    public static void WriteSeeds(string directory)
    {
        for (int i = 0; i < 16; i++)
        {
            var bytes = new byte[2048];
            new Random(1000 + i).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(directory, $"seed{i:00}"), bytes);
        }
    }

    public sealed record CaseStats(int Documents, int Queries, long Hits, bool Merged, int Segments, int ExpectedErrors);

    private sealed class Choice(byte[] data)
    {
        private int _pos;

        public int Next(int n)
        {
            if (n <= 1)
            {
                return 0;
            }

            int v = _pos < data.Length ? data[_pos++] : 0;
            if (n > 256)
            {
                v = (v << 8) | (_pos < data.Length ? data[_pos++] : 0);
            }

            return v % n;
        }

        public bool Chance(int oneIn) => Next(oneIn) == 0;

        public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];
    }

    private sealed record RDoc(string Id, string[] Title, string[] Body, string? Tag, long? Num, double? Score, long? When, bool? Flag);

    private static CaseStats RunCase(byte[] bytes)
    {
        var c = new Choice(bytes);
        Schema schema = new SchemaBuilder().Id("id")
            .Add(new FieldDefinition("title", FieldType.Text) { Analyzer = "whitespace-lowercase", Boost = 2f })
            .Add(new FieldDefinition("body", FieldType.Text) { Analyzer = "whitespace-lowercase" })
            .Add(new FieldDefinition("tag", FieldType.Keyword) { Boost = 1.5f })
            .Integral("num").Fractional("score").Date("when").Bool("flag")
            .Build();

        var docs = new List<RDoc>();
        int count = 6 + c.Next(50);
        for (int i = 0; i < count; i++)
        {
            docs.Add(new RDoc("d" + i, MakeWords(c, c.Next(5)), MakeWords(c, c.Next(14)), c.Chance(4) ? null : c.Pick(Tags),
                c.Chance(5) ? null : c.Next(50), c.Chance(5) ? null : c.Pick(Doubles), c.Chance(5) ? null : 1_000_000L * c.Next(30), c.Chance(5) ? null : c.Chance(2)));
        }

        var storage = new MemoryStorage();
        var options = new IndexWriterOptions { MaxBufferedDocuments = 3 + c.Next(8), AutoMerge = false };
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        bool merge;
        using (IndexWriter writer = IndexWriter.Open(storage, schema, options))
        {
            int firstWave = count * 2 / 3;
            for (int i = 0; i < firstWave; i++)
            {
                writer.Upsert(ToDocument(docs[i]));
                if (c.Chance(12))
                {
                    writer.Commit();
                }
            }

            writer.Commit();
            for (int i = 0; i < firstWave; i++)
            {
                if (c.Chance(4))
                {
                    writer.DeleteById(docs[i].Id);
                    deleted.Add(docs[i].Id);
                }
            }

            writer.Commit();
            for (int i = firstWave; i < count; i++)
            {
                writer.Upsert(ToDocument(docs[i]));
            }

            writer.Commit();
            merge = c.Chance(2);
            if (merge)
            {
                writer.ForceMerge(1);
                writer.Commit();
            }
        }

        using IndexSnapshot snapshot = IndexSnapshot.Open(storage);
        List<RDoc> live = [.. docs.Where(d => !deleted.Contains(d.Id))];
        // Deleted documents stay in their segment (and in the statistics) until a merge rewrites it, and a segment whose documents are
        // all deleted disappears, so ask the segments' identifier maps which documents are physically there.
        List<RDoc> physical = [.. docs.Where(d => Enumerable.Range(0, snapshot.SegmentCount).Any(s => snapshot.GetSegment(s).Ids!.TryFind(Encoding.UTF8.GetBytes(d.Id), out _)))];
        if (snapshot.LiveDocCount != live.Count)
        {
            throw new InvalidOperationException($"The snapshot has {snapshot.LiveDocCount} live documents; expected {live.Count}.");
        }

        IndexSearcher searcher = SnapshotSearch.CreateSearcher(snapshot);
        var reference = new Reference(physical, live, Bm25Parameters.Default);
        var globalToId = new Dictionary<int, string>();
        foreach (RDoc d in live)
        {
            if (!snapshot.TryFindById(d.Id, out int seg, out int local))
            {
                throw new InvalidOperationException($"Live document {d.Id} not found by id.");
            }

            globalToId[searcher.Context.Leaves[seg].DocBase + local] = d.Id;
        }

        int queries = 3 + c.Next(6);
        long hits = 0;
        int errors = 0;
        for (int q = 0; q < queries; q++)
        {
            if (c.Chance(30))
            {
                Query bad = BadQuery(c);
                try
                {
                    searcher.Search(bad, 5);
                    throw new InvalidOperationException($"{bad} should have been rejected.");
                }
                catch (QueryException)
                {
                    errors++;
                }

                continue;
            }

            Query query = MakeQuery(c, 0);
            hits += Check(searcher, reference, live, globalToId, query, c);
        }

        return new CaseStats(count, queries, hits, merge, snapshot.SegmentCount, errors);
    }

    private static string[] MakeWords(Choice c, int n) => [.. Enumerable.Range(0, n).Select(_ => c.Pick(Words))];

    private static Document ToDocument(RDoc d)
    {
        var doc = new Document().Add("id", d.Id);
        if (d.Title.Length > 0)
        {
            doc.Add("title", string.Join(' ', d.Title));
        }

        if (d.Body.Length > 0)
        {
            doc.Add("body", string.Join(' ', d.Body));
        }

        if (d.Tag is not null)
        {
            doc.Add("tag", d.Tag);
        }

        if (d.Num is { } n)
        {
            doc.Add("num", n);
        }

        if (d.Score is { } s)
        {
            doc.Add("score", s);
        }

        if (d.When is { } w)
        {
            doc.Add("when", DateTimeOffset.FromUnixTimeMilliseconds(w));
        }

        if (d.Flag is { } f)
        {
            doc.Add("flag", f);
        }

        return doc;
    }

    // ---- query generation ----

    private static float PickBoost(Choice c) => c.Next(6) switch { 0 => 0.5f, 1 => 2f, 2 => 0f, _ => 1f };

    private static string PickTerm(Choice c) => c.Chance(10) ? "zzz" : c.Pick(Words);

    private static Query MakeQuery(Choice c, int depth)
    {
        int kind = c.Next(depth >= 2 ? 11 : 13);
        switch (kind)
        {
            case 0:
            case 1:
                return new TermQuery(c.Chance(5) ? "tag" : c.Pick(["title", "body"]), PickTerm(c)) { Boost = PickBoost(c) };
            case 2:
                return new MultiFieldTermQuery(["title", "body"], PickTerm(c)) { Boost = PickBoost(c) };
            case 3:
                return new MultiFieldTermQuery(["body", "title", "tag"], c.Chance(2) ? c.Pick(Tags) : PickTerm(c)) { Boost = PickBoost(c) };
            case 4:
            case 5:
                return new PhraseQuery(c.Pick(["title", "body"]), [.. Enumerable.Range(0, 1 + c.Next(3)).Select(_ => PickTerm(c))]) { Slop = c.Chance(2) ? 0 : c.Next(4), Boost = PickBoost(c) };
            case 6:
                return new PrefixQuery(c.Pick(["title", "body"]), c.Pick(["r", "ru", "run", "ap", "a", "z", "gam", "q"])) { Boost = PickBoost(c), ScoreTerms = c.Chance(2) };
            case 7:
                return new FuzzyQuery(c.Pick(["title", "body"]), c.Pick(["runn", "aple", "gama", "run", "bta", "zta", "alpah", "runnig"])) { MaxEdits = c.Next(3), PrefixLength = c.Chance(3) ? 1 + c.Next(2) : 0, Transpositions = !c.Chance(3), Boost = PickBoost(c) };
            case 8:
                return new RegexQuery(c.Pick(["title", "body"]), c.Pick(RegexPatterns)) { Boost = PickBoost(c) };
            case 9:
                return MakeRange(c);
            case 10:
                return new MatchAllQuery { Boost = PickBoost(c) };
            default:
                {
                    int n = c.Next(5);
                    var clauses = new List<BooleanClause>();
                    for (int i = 0; i < n; i++)
                    {
                        Occur o = c.Next(8) switch { 0 or 1 or 2 => Occur.Should, 3 or 4 => Occur.Must, 5 => Occur.MustNot, _ => Occur.Filter };
                        clauses.Add(new BooleanClause(MakeQuery(c, depth + 1), o));
                    }

                    return new BooleanQuery(clauses) { MinimumShouldMatch = c.Chance(3) ? c.Next(3) : 0, Boost = PickBoost(c) };
                }
        }
    }

    private static RangeQuery MakeRange(Choice c)
    {
        FieldValue? Bound(Func<FieldValue> make) => c.Chance(4) ? null : make();
        switch (c.Next(5))
        {
            case 0:
                return new RangeQuery("num", Bound(() => FieldValue.FromInt64(c.Next(50))), Bound(() => FieldValue.FromInt64(c.Next(50)))) { IncludeLower = c.Chance(2), IncludeUpper = c.Chance(2), Boost = PickBoost(c) };
            case 1:
                return new RangeQuery("score", Bound(() => c.Chance(2) ? FieldValue.FromFloat64(c.Pick(Doubles)) : FieldValue.FromInt64(c.Next(8))), Bound(() => FieldValue.FromFloat64(c.Pick(Doubles)))) { IncludeLower = c.Chance(2), IncludeUpper = c.Chance(2), Boost = PickBoost(c) };
            case 2:
                return new RangeQuery("when", Bound(() => FieldValue.FromDate(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000L * c.Next(30)))), Bound(() => FieldValue.FromDate(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000L * c.Next(30))))) { IncludeLower = c.Chance(2), IncludeUpper = c.Chance(2), Boost = PickBoost(c) };
            case 3:
                return new RangeQuery("flag", Bound(() => FieldValue.FromBool(c.Chance(2))), Bound(() => FieldValue.FromBool(c.Chance(2)))) { IncludeLower = c.Chance(2), IncludeUpper = c.Chance(2), Boost = PickBoost(c) };
            default:
                return new RangeQuery("tag", Bound(() => FieldValue.FromText(c.Pick(Tags))), Bound(() => FieldValue.FromText(c.Pick(Tags)))) { IncludeLower = c.Chance(2), IncludeUpper = c.Chance(2), Boost = PickBoost(c) };
        }
    }

    private static Query BadQuery(Choice c) => c.Next(8) switch
    {
        0 => new TermQuery("nosuchfield", "alpha"),
        1 => new TermQuery("num", "5"),
        2 => new FuzzyQuery("body", "alpha") { MaxEdits = 3 },
        3 => new RegexQuery("body", "(unclosed"),
        4 => new RangeQuery("num", FieldValue.FromText("x"), null),
        5 => new RangeQuery("body", null, null),
        6 => new TermQuery("body", "alpha") { Boost = -1f },
        _ => new BooleanQuery([new BooleanClause(new PhraseQuery("tag", ["a", "b"]), Occur.Must)]),
    };

    // ---- comparison ----

    private static long Check(IndexSearcher searcher, Reference reference, List<RDoc> live, Dictionary<int, string> globalToId, Query query, Choice c)
    {
        Dictionary<string, double> expected = reference.Evaluate(query);
        int all = Math.Max(1, live.Count);
        TopDocs full = searcher.Search(query, all, countExactly: true);
        string label = Describe(query);
        if (!full.TotalHitsExact || full.TotalHits != expected.Count || full.Hits.Count != expected.Count)
        {
            throw new InvalidOperationException($"Hit count {full.TotalHits}/{full.Hits.Count} differs from the reference's {expected.Count} for {label}; smallest differing part: {Localize(searcher, reference, live, globalToId, query)}");
        }

        ScoreDoc previous = default;
        bool first = true;
        foreach (ScoreDoc h in full.Hits)
        {
            if (!globalToId.TryGetValue(h.DocId, out string? id) || !expected.TryGetValue(id, out double want))
            {
                throw new InvalidOperationException($"Document {h.DocId} should not match {label}.");
            }

            if (Math.Abs(h.Score - want) > 1e-4 * Math.Max(1.0, Math.Abs(want)))
            {
                throw new InvalidOperationException($"Document {id} scored {h.Score}, the reference says {want}, for {label}.");
            }

            if (!first && (h.Score > previous.Score || (h.Score == previous.Score && h.DocId < previous.DocId)))
            {
                throw new InvalidOperationException($"Hits are out of order for {label}.");
            }

            previous = h;
            first = false;
        }

        // Pruned top-k pages must equal the head of the exhaustive list, bit for bit.
        foreach (int k in new[] { 1, 3, 10 })
        {
            TopDocs page = searcher.Search(query, k, countExactly: false);
            int n = Math.Min(k, full.Hits.Count);
            if (page.Hits.Count != n || page.TotalHits > full.TotalHits)
            {
                throw new InvalidOperationException($"The top-{k} page has {page.Hits.Count} hits (total {page.TotalHits}); expected {n} for {label}.");
            }

            for (int i = 0; i < n; i++)
            {
                if (page.Hits[i].DocId != full.Hits[i].DocId || BitConverter.SingleToInt32Bits(page.Hits[i].Score) != BitConverter.SingleToInt32Bits(full.Hits[i].Score))
                {
                    throw new InvalidOperationException($"Top-{k} hit {i} is {page.Hits[i]}, the exhaustive list has {full.Hits[i]}, for {label}.");
                }
            }
        }

        // Explanations: equal to the score for matches, absent for non-matches.
        for (int i = 0; i < Math.Min(3, full.Hits.Count); i++)
        {
            ScoreDoc h = full.Hits[c.Next(full.Hits.Count)];
            Explanation? e = searcher.Explain(query, h.DocId);
            if (e is null || e.Value != h.Score)
            {
                throw new InvalidOperationException($"Explanation {e?.Value.ToString() ?? "none"} differs from score {h.Score} of {globalToId[h.DocId]} for {label}.");
            }
        }

        foreach (KeyValuePair<int, string> kv in globalToId)
        {
            if (!expected.ContainsKey(kv.Value) && c.Chance(8) && searcher.Explain(query, kv.Key) is not null)
            {
                throw new InvalidOperationException($"{kv.Value} does not match {label} but has an explanation.");
            }
        }

        return full.Hits.Count;
    }

    // Finds the innermost sub-query whose hit set already differs from the reference's.
    private static string Localize(IndexSearcher searcher, Reference reference, List<RDoc> live, Dictionary<int, string> globalToId, Query query)
    {
        if (query is BooleanQuery b)
        {
            foreach (BooleanClause c in b.Clauses)
            {
                string inner = Localize(searcher, reference, live, globalToId, c.Query);
                if (inner.Length > 0)
                {
                    return inner;
                }
            }
        }

        HashSet<string> want = [.. reference.Evaluate(query).Keys];
        TopDocs got = searcher.Search(query, Math.Max(1, live.Count), countExactly: true);
        HashSet<string> have = [.. got.Hits.Select(h => globalToId[h.DocId])];
        return want.SetEquals(have) ? string.Empty : $"{Describe(query)}: engine only [{string.Join(' ', have.Except(want).Order())}], reference only [{string.Join(' ', want.Except(have).Order())}]";
    }

    private static string Describe(Query q) => q switch
    {
        BooleanQuery b => $"bool(boost {b.Boost}, msm {b.MinimumShouldMatch}: {string.Join(", ", b.Clauses.Select(c => c.Occur + " " + Describe(c.Query)))})",
        PhraseQuery p => $"phrase({p.Field} [{string.Join(' ', p.Terms)}] slop {p.Slop} boost {p.Boost})",
        MultiFieldTermQuery m => $"terms({string.Join('+', m.Fields)}:{m.Term} boost {m.Boost})",
        _ => q.ToString() ?? string.Empty,
    };

    // ---- the reference ----

    private sealed class Reference
    {
        private readonly List<RDoc> _live;
        private readonly Dictionary<string, FieldStat> _stats = new(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _weights = new(StringComparer.Ordinal) { ["title"] = 2f, ["body"] = 1f, ["tag"] = 1.5f };
        private readonly double _k1;
        private readonly double _b;

        public Reference(List<RDoc> physical, List<RDoc> live, Bm25Parameters p)
        {
            _live = live;
            _k1 = p.K1;
            _b = p.B;
            foreach (string f in new[] { "title", "body", "tag" })
            {
                var s = new FieldStat();
                foreach (RDoc d in physical)
                {
                    string[] t = Tokens(d, f);
                    if (t.Length == 0)
                    {
                        continue;
                    }

                    s.DocCount++;
                    s.SumLength += t.Length;
                    foreach (string term in t.Distinct())
                    {
                        s.Df[term] = s.Df.GetValueOrDefault(term) + 1;
                    }
                }

                _stats[f] = s;
            }
        }

        private sealed class FieldStat
        {
            public int DocCount;
            public long SumLength;
            public Dictionary<string, int> Df { get; } = new(StringComparer.Ordinal);
        }

        private static string[] Tokens(RDoc d, string field) => field switch
        {
            "title" => d.Title,
            "body" => d.Body,
            _ => d.Tag is null ? [] : [d.Tag],
        };

        public Dictionary<string, double> Evaluate(Query q)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (RDoc d in _live)
            {
                if (Score(q, d) is { } s)
                {
                    result[d.Id] = s;
                }
            }

            return result;
        }

        private double Idf(IEnumerable<string> fields, string term)
        {
            long n = 0;
            long big = 0;
            foreach (string f in fields)
            {
                big = Math.Max(big, _stats[f].DocCount);
                n = Math.Max(n, _stats[f].Df.GetValueOrDefault(term));
            }

            big = Math.Max(big, n);
            return Math.Log(1.0 + ((big - n + 0.5) / (n + 0.5)));
        }

        private double Norm(string field, int length)
        {
            FieldStat s = _stats[field];
            double avg = s.DocCount > 0 && s.SumLength > 0 ? (double)s.SumLength / s.DocCount : 1.0;
            return (1 - _b) + (_b * length / avg);
        }

        private double? TermScore(string[] fields, string term, double boost, RDoc d)
        {
            double x = 0;
            bool any = false;
            foreach (string f in fields)
            {
                string[] t = Tokens(d, f);
                int tf = t.Count(w => w == term);
                if (tf > 0)
                {
                    any = true;
                    x += _weights[f] * tf / Norm(f, t.Length);
                }
            }

            return any ? boost * Idf(fields, term) * (x / (_k1 + x)) : null;
        }

        private double? Score(Query q, RDoc d)
        {
            switch (q)
            {
                case TermQuery t:
                    return TermScore([t.Field], t.Term, t.Boost, d);
                case MultiFieldTermQuery m:
                    return TermScore([.. m.Fields], m.Term, m.Boost, d);
                case PhraseQuery p:
                    return PhraseScore(p, d);
                case PrefixQuery p:
                    {
                        double? terms = Expansion(p.Field, p.Boost, d, t => t.StartsWith(p.Prefix, StringComparison.Ordinal));
                        return p.ScoreTerms || terms is null ? terms : p.Boost; // a constant score unless the terms are scored
                    }
                case FuzzyQuery f:
                    return Expansion(f.Field, f.Boost, d, t => Fuzzy(f, t));
                case RegexQuery r:
                    {
                        var re = new Regex("^(?:" + r.Pattern + ")$", RegexOptions.CultureInvariant);
                        return Expansion(r.Field, r.Boost, d, re.IsMatch);
                    }

                case MatchAllQuery m:
                    return m.Boost;
                case RangeQuery r:
                    return InRange(r, d) ? r.Boost : null;
                case BooleanQuery b:
                    return BooleanScore(b, d);
                default:
                    throw new InvalidOperationException("unknown query " + q);
            }
        }

        private double? BooleanScore(BooleanQuery b, RDoc d)
        {
            bool hasRequired = b.Clauses.Any(c => c.Occur is Occur.Must or Occur.Filter);
            int needed = b.MinimumShouldMatch > 0 ? b.MinimumShouldMatch : (hasRequired ? 0 : 1);
            int shouldCount = b.Clauses.Count(c => c.Occur == Occur.Should);
            if (b.Clauses.Count == 0 || shouldCount < needed)
            {
                return null;
            }

            double sum = 0;
            int matched = 0;
            foreach (BooleanClause c in b.Clauses)
            {
                double? s = Score(c.Query, d);
                switch (c.Occur)
                {
                    case Occur.Must:
                        if (s is null)
                        {
                            return null;
                        }

                        sum += s.Value;
                        break;
                    case Occur.Filter:
                        if (s is null)
                        {
                            return null;
                        }

                        break;
                    case Occur.Should:
                        if (s is not null)
                        {
                            matched++;
                            sum += s.Value;
                        }

                        break;
                    default:
                        if (s is not null)
                        {
                            return null;
                        }

                        break;
                }
            }

            if (matched < needed || (!hasRequired && matched == 0))
            {
                return null;
            }

            return sum * b.Boost;
        }

        private double? Expansion(string field, float boost, RDoc d, Func<string, bool> accepts)
        {
            // Terms of the field as the index has them (deleted documents included until a merge), in byte order.
            var vocabulary = _stats[field].Df.Keys.Where(accepts).OrderBy(t => t, StringComparer.Ordinal).ToList();
            double sum = 0;
            bool any = false;
            foreach (string term in vocabulary)
            {
                if (TermScore([field], term, 1.0, d) is { } s)
                {
                    sum += s;
                    any = true;
                }
            }

            return any ? sum * boost : null;
        }

        private static bool Fuzzy(FuzzyQuery f, string term)
        {
            int[] a = [.. f.Term.EnumerateRunes().Select(r => r.Value)];
            int[] b = [.. term.EnumerateRunes().Select(r => r.Value)];
            if (f.MaxEdits == 0)
            {
                return f.Term == term;
            }

            int prefix = Math.Min(f.PrefixLength, a.Length);
            if (b.Length < prefix || !a.Take(prefix).SequenceEqual(b.Take(prefix)))
            {
                return false;
            }

            return OptimalStringAlignment(a[prefix..], b[prefix..], f.Transpositions) <= f.MaxEdits;
        }

        private static int OptimalStringAlignment(int[] a, int[] b, bool transpositions)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++)
            {
                d[i, 0] = i;
            }

            for (int j = 0; j <= b.Length; j++)
            {
                d[0, j] = j;
            }

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (transpositions && i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    {
                        d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                    }
                }
            }

            return d[a.Length, b.Length];
        }

        private double? PhraseScore(PhraseQuery p, RDoc d)
        {
            if (p.Terms.Count == 0)
            {
                return null;
            }

            if (p.Terms.Count == 1)
            {
                return TermScore([p.Field], p.Terms[0], p.Boost, d);
            }

            string[] tokens = Tokens(d, p.Field);
            var positions = p.Terms.Select(t => Enumerable.Range(0, tokens.Length).Where(i => tokens[i] == t).ToArray()).ToArray();
            double freq = 0;
            foreach (int start in positions[0])
            {
                int best = int.MaxValue;
                Search(1, start);
                void Search(int index, int previous)
                {
                    if (index == positions.Length)
                    {
                        best = Math.Min(best, previous - start - (positions.Length - 1));
                        return;
                    }

                    foreach (int pos in positions[index])
                    {
                        if (pos > previous)
                        {
                            Search(index + 1, pos);
                        }
                    }
                }

                if (best != int.MaxValue && best <= p.Slop)
                {
                    freq += 1.0 / (1 + best);
                }
            }

            if (freq == 0)
            {
                return null;
            }

            double idf = p.Terms.Sum(t => Idf([p.Field], t));
            double x = _weights[p.Field] * freq / Norm(p.Field, tokens.Length);
            return p.Boost * idf * (x / (_k1 + x));
        }

        private static bool InRange(RangeQuery r, RDoc d)
        {
            static bool Within<T>(T value, T? lo, T? hi, RangeQuery q)
                where T : struct, IComparable<T>
            {
                if (lo is { } l && (value.CompareTo(l) < 0 || (value.CompareTo(l) == 0 && !q.IncludeLower)))
                {
                    return false;
                }

                return !(hi is { } h && (value.CompareTo(h) > 0 || (value.CompareTo(h) == 0 && !q.IncludeUpper)));
            }

            switch (r.Field)
            {
                case "num":
                    return d.Num is { } n && Within(n, r.Lower?.AsInt64(), r.Upper?.AsInt64(), r);
                case "score":
                    return d.Score is { } s && Within(s, r.Lower?.AsFloat64(), r.Upper?.AsFloat64(), r);
                case "when":
                    return d.When is { } w && Within(w, r.Lower?.AsInt64(), r.Upper?.AsInt64(), r);
                case "flag":
                    return d.Flag is { } f && Within(f ? 1 : 0, r.Lower is { } l ? (l.AsBool() ? 1 : 0) : null, r.Upper is { } u ? (u.AsBool() ? 1 : 0) : null, r);
                default:
                    {
                        if (d.Tag is not { } tag)
                        {
                            return false;
                        }

                        if (r.Lower is { } lo && (string.CompareOrdinal(tag, lo.AsText()) is var c1) && (c1 < 0 || (c1 == 0 && !r.IncludeLower)))
                        {
                            return false;
                        }

                        return !(r.Upper is { } hi && (string.CompareOrdinal(tag, hi.AsText()) is var c2) && (c2 > 0 || (c2 == 0 && !r.IncludeUpper)));
                    }
            }
        }
    }
}
