using System.Text;
using OneIndex.Codecs;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

/// <summary>A seeded random corpus with its expected postings kept independently of the code under test.</summary>
internal sealed class TextCorpus
{
    public const string Body = "body";
    public const string Title = "title";
    public const string Tag = "tag";

    public sealed record Occurrence(int Position, int Start, int End);

    public sealed class DocPostings
    {
        public int Doc { get; init; }

        public List<Occurrence> Occurrences { get; } = [];
    }

    public int MaxDoc { get; private set; }

    /// <summary>field → term bytes (as a hex-free string key via Latin1) → postings, kept in UTF-8 byte order.</summary>
    public Dictionary<string, SortedDictionary<byte[], List<DocPostings>>> Fields { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, int[]> Lengths { get; } = new(StringComparer.Ordinal);

    public static PostingsFeatures FeaturesOf(string field) => field switch
    {
        Body => PostingsFeatures.Offsets,
        Title => PostingsFeatures.Positions,
        _ => PostingsFeatures.Frequencies,
    };

    public static readonly IComparer<byte[]> ByteOrder = Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b));

    public static string[] MakeVocabulary(Random rng, int size)
    {
        string[] alphabets =
        [
            "abcdefghijklmnopqrstuvwxyz",
            "abcdefghijklmnopqrstuvwxyzéèüöñßçæø",
            "абвгдежзиклмнопрстуфхцчшщыэюя",
            "日本語中文字漢語한국어",
            "αβγδεζηθικλμνξοπρστυφχψω",
        ];
        var set = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        while (list.Count < size)
        {
            string s;
            int kind = rng.Next(100);
            if (kind < 4)
            {
                // Supplementary-plane characters (surrogate pairs) sort differently in UTF-16 and in UTF-8.
                var sb = new StringBuilder();
                int n = rng.Next(1, 4);
                for (int i = 0; i < n; i++)
                {
                    sb.Append(char.ConvertFromUtf32(0x1F600 + rng.Next(40)));
                }

                if (rng.Next(2) == 0)
                {
                    sb.Append('\uFF21');
                }

                s = sb.ToString();
            }
            else if (kind < 5 && list.Count > 10)
            {
                s = new string((char)('a' + rng.Next(3)), 4000 + rng.Next(2000)) + rng.Next(1000);
            }
            else
            {
                string alphabet = alphabets[kind < 70 ? 0 : rng.Next(alphabets.Length)];
                int len = 1 + rng.Next(rng.Next(4) == 0 ? 14 : 8);
                var chars = new char[len];
                for (int i = 0; i < len; i++)
                {
                    chars[i] = alphabet[rng.Next(alphabet.Length)];
                }

                s = new string(chars);
            }

            if (set.Add(s))
            {
                list.Add(s);
            }
        }

        return [.. list];
    }

    /// <summary>Generates documents whose tokens follow a Zipf-like law; term 0 is in every document of the body field.</summary>
    public static TextCorpus Generate(int seed, int docs, int vocabularySize, double emptyFraction = 0.05)
    {
        var rng = new Random(seed);
        string[] vocab = MakeVocabulary(rng, vocabularySize);
        byte[][] vocabBytes = [.. vocab.Select(Encoding.UTF8.GetBytes)];
        var cumulative = new double[vocabularySize];
        double total = 0;
        for (int i = 0; i < vocabularySize; i++)
        {
            total += 1.0 / (i + 1);
            cumulative[i] = total;
        }

        int Pick()
        {
            double x = rng.NextDouble() * total;
            int lo = 0;
            int hi = vocabularySize - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (cumulative[mid] >= x)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            return lo;
        }

        var c = new TextCorpus { MaxDoc = docs };
        foreach (string field in new[] { Body, Title, Tag })
        {
            c.Fields[field] = new SortedDictionary<byte[], List<DocPostings>>(ByteOrder);
            c.Lengths[field] = new int[docs];
        }

        for (int doc = 0; doc < docs; doc++)
        {
            foreach (string field in new[] { Body, Title, Tag })
            {
                if (rng.NextDouble() < emptyFraction && field != Body)
                {
                    continue;
                }

                int count = field == Body ? 3 + rng.Next(40) : field == Title ? 1 + rng.Next(6) : rng.Next(1, 4);
                int position = -1;
                int offset = 0;
                for (int t = 0; t < count; t++)
                {
                    int term = field == Body && t == 0 ? 0 : Pick();
                    bool stacked = t > 0 && rng.Next(20) == 0;
                    if (!stacked)
                    {
                        position++;
                        offset += 1 + rng.Next(3);
                    }

                    int start = offset;
                    int end = start + Math.Min(vocab[term].Length, 30);
                    offset = end;
                    c.Add(field, vocabBytes[term], doc, position, start, end);
                    c.Lengths[field][doc]++;
                }
            }
        }

        return c;
    }

    public void Add(string field, byte[] term, int doc, int position, int start, int end)
    {
        SortedDictionary<byte[], List<DocPostings>> terms = Fields[field];
        if (!terms.TryGetValue(term, out List<DocPostings>? list))
        {
            list = [];
            terms.Add(term, list);
        }

        if (list.Count == 0 || list[^1].Doc != doc)
        {
            list.Add(new DocPostings { Doc = doc });
        }

        list[^1].Occurrences.Add(new Occurrence(position, start, end));
    }

    public InvertedBuffer ToBuffer()
    {
        var buffer = new InvertedBuffer();
        foreach (string field in Fields.Keys)
        {
            buffer.DeclareField(field, FeaturesOf(field));
        }

        // Feed tokens document by document, in position order within the document, as an analyzer would.
        foreach (string field in Fields.Keys)
        {
            var perDoc = new SortedDictionary<int, List<(int Pos, int Start, int End, byte[] Term)>>();
            foreach ((byte[] term, List<DocPostings> postings) in Fields[field])
            {
                foreach (DocPostings dp in postings)
                {
                    if (!perDoc.TryGetValue(dp.Doc, out var l))
                    {
                        perDoc[dp.Doc] = l = [];
                    }

                    foreach (Occurrence o in dp.Occurrences)
                    {
                        l.Add((o.Position, o.Start, o.End, term));
                    }
                }
            }

            foreach ((int doc, var tokens) in perDoc)
            {
                foreach (var t in tokens.OrderBy(static x => x.Pos).ThenBy(static x => x.Start))
                {
                    buffer.AddToken(doc, field, t.Term, t.Pos, t.Start, t.End);
                }
            }
        }

        return buffer;
    }

    /// <summary>A corpus of only the documents not in <paramref name="deleted"/>, renumbered densely in order.</summary>
    public TextCorpus Without(ISet<int> deleted, out int[] docMap)
    {
        docMap = new int[MaxDoc];
        int next = 0;
        for (int d = 0; d < MaxDoc; d++)
        {
            docMap[d] = deleted.Contains(d) ? -1 : next++;
        }

        var result = new TextCorpus { MaxDoc = next };
        foreach ((string field, var terms) in Fields)
        {
            result.Fields[field] = new SortedDictionary<byte[], List<DocPostings>>(ByteOrder);
            result.Lengths[field] = new int[next];
            for (int d = 0; d < MaxDoc; d++)
            {
                if (docMap[d] >= 0)
                {
                    result.Lengths[field][docMap[d]] = Lengths[field][d];
                }
            }

            foreach ((byte[] term, List<DocPostings> postings) in terms)
            {
                foreach (DocPostings dp in postings)
                {
                    if (docMap[dp.Doc] < 0)
                    {
                        continue;
                    }

                    foreach (Occurrence o in dp.Occurrences)
                    {
                        result.Add(field, term, docMap[dp.Doc], o.Position, o.Start, o.End);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>The documents of several corpora one after another, as a merge numbers them.</summary>
    public static TextCorpus Concat(IReadOnlyList<TextCorpus> parts)
    {
        int total = parts.Sum(static p => p.MaxDoc);
        var result = new TextCorpus { MaxDoc = total };
        foreach (string field in new[] { Body, Title, Tag })
        {
            result.Fields[field] = new SortedDictionary<byte[], List<DocPostings>>(ByteOrder);
            result.Lengths[field] = new int[total];
        }

        int offset = 0;
        foreach (TextCorpus part in parts)
        {
            foreach ((string field, var terms) in part.Fields)
            {
                Array.Copy(part.Lengths[field], 0, result.Lengths[field], offset, part.MaxDoc);
                foreach ((byte[] term, List<DocPostings> postings) in terms)
                {
                    foreach (DocPostings dp in postings)
                    {
                        foreach (Occurrence o in dp.Occurrences)
                        {
                            result.Add(field, term, dp.Doc + offset, o.Position, o.Start, o.End);
                        }
                    }
                }
            }

            offset += part.MaxDoc;
        }

        return result;
    }

    public static readonly string[] TermsFiles = ["seg.terms", "seg.postings", "seg.lengths"];

    public TextSegmentFiles Write(IStorage storage, string prefix = "seg", int termsVersion = 2)
    {
        using var writer = new TextSegmentWriter(storage, prefix + ".terms", prefix + ".postings", prefix + ".lengths", MaxDoc, termsVersion: termsVersion);
        ToBuffer().WriteTo(writer);
        return writer.Finish();
    }

    public static TextSegmentReader Open(IStorage storage, string prefix = "seg")
        => TextSegmentReader.Open(storage, prefix + ".terms", prefix + ".postings", prefix + ".lengths");
}
