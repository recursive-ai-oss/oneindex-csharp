using System.Buffers.Binary;
using System.Text;
using OneIndex.Codecs;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Fuzz;

/// <summary>
/// Feeds arbitrary bytes in place of the terms file or the postings file of a valid segment, then reads everything the damaged
/// segment offers: every term, every posting with positions and offsets, advances and look-aheads, prefixes, fuzzy and regular
/// expression walks. Only <see cref="CorruptIndexException"/> may come out; a crash, a hang or an allocation past the limits is a finding.
/// </summary>
internal static class TextIndexTarget
{
    private static readonly IndexLimits Limits = new() { MaxTermBytes = 4_096, MaxResidentFileBytes = 1 << 22, MaxDocsPerSegment = 100_000 };
    private static readonly Lazy<Baseline> Good = new(BuildBaseline);

    private sealed record Baseline(byte[] Terms, byte[] Postings, byte[] Lengths);

    /// <summary>The first input byte picks whether the footer and checksum are repaired (odd) so that mutations reach the structure parsers, or left as given.</summary>
    public static void RunTerms(ReadOnlySpan<byte> data) => Run(data, terms: true);

    /// <inheritdoc cref="RunTerms"/>
    public static void RunPostings(ReadOnlySpan<byte> data) => Run(data, terms: false);

    private static void Run(ReadOnlySpan<byte> data, bool terms)
    {
        if (data.IsEmpty)
        {
            return;
        }

        Baseline good = Good.Value;
        byte[] file = data[1..].ToArray();
        if ((data[0] & 1) != 0)
        {
            Repair(file, terms ? TextSegmentWriter.TermsMagic : TextSegmentWriter.PostingsMagic);
        }

        var storage = new MemoryStorage();
        Put(storage, "s.terms", terms ? file : good.Terms);
        Put(storage, "s.postings", terms ? good.Postings : file);
        Put(storage, "s.lengths", good.Lengths);
        Exercise(storage);
    }

    /// <summary>Opens the three files and reads all of them, treating <see cref="CorruptIndexException"/> as the expected outcome for damaged input.</summary>
    public static void Exercise(IStorage storage)
    {
        try
        {
            using TextSegmentReader reader = TextSegmentReader.Open(storage, "s.terms", "s.postings", "s.lengths", Limits);
            foreach (string name in reader.FieldNames)
            {
                ITextFieldReader field = reader.GetTextField(name)!;
                _ = field.Statistics;
                ReadAllTerms(field, reader.MaxDoc);
                foreach (string prefix in new[] { "a", "ab", "\u00e9", "z" })
                {
                    Drain(field.GetTerms(Encoding.UTF8.GetBytes(prefix)));
                }

                Drain(field.Intersect(new LevenshteinAutomaton("abc", 2)));
                Drain(field.Intersect(new RegexAutomaton("[a-c]+.*"), "a"u8));
            }
        }
        catch (CorruptIndexException)
        {
            // Expected for damaged input.
        }
    }

    private static void ReadAllTerms(ITextFieldReader field, int maxDoc)
    {
        PostingsFeatures features = field.IndexedFeatures;
        using ITermEnumerator terms = field.GetTerms(default);
        int termCount = 0;
        while (terms.MoveNext() && termCount++ < 2_000)
        {
            TermInfo info = terms.Info;
            _ = field.TryGetTerm(terms.Term, out _);
            PostingsIterator it = field.GetPostings(info, features);
            int visited = 0;
            for (int doc = it.NextDoc(); doc != DocIdSetIterator.NoMoreDocs && visited < 5_000; doc = it.Advance(doc + 1 + (visited & 7)), visited++)
            {
                int freq = it.Frequency;
                if ((features & PostingsFeatures.Positions) == PostingsFeatures.Positions)
                {
                    for (int k = 0; k < Math.Min(freq, 64); k++)
                    {
                        it.NextPosition();
                    }
                }

                _ = it.ShallowAdvance(doc);
                if (doc < maxDoc)
                {
                    _ = field.GetFieldLength(doc);
                }
            }
        }
    }

    private static void Drain(ITermEnumerator e)
    {
        using (e)
        {
            int n = 0;
            while (e.MoveNext() && n++ < 2_000)
            {
                _ = e.Info;
            }
        }
    }

    private static void Put(MemoryStorage storage, string name, byte[] bytes)
    {
        using IStorageWriter w = storage.Create(name);
        w.Write(bytes);
    }

    private static void Repair(byte[] file, uint magic)
    {
        if (file.Length < IndexFile.HeaderLength + IndexFile.FooterLength)
        {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(file, magic);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 1);
        Span<byte> footer = file.AsSpan(file.Length - IndexFile.FooterLength);
        BinaryPrimitives.WriteUInt32LittleEndian(footer, IndexFile.FooterMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[4..], IndexFile.AlgorithmCrc32C);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[8..], Crc32C.Compute(file.AsSpan(0, file.Length - 4)));
    }

    private static Baseline BuildBaseline()
    {
        var rng = new Random(12345);
        var buffer = new InvertedBuffer();
        buffer.DeclareField("body", PostingsFeatures.Offsets);
        buffer.DeclareField("tag", PostingsFeatures.Frequencies);
        string[] words = ["abc", "abd", "abcd", "b", "bc", "cab", "\u00e9t\u00e9", "zebra", "a", "ab", "xyz", "caf\u00e9"];
        const int docs = 300;
        for (int d = 0; d < docs; d++)
        {
            int position = 0;
            int offset = 0;
            buffer.AddToken(d, "body", "abc"u8, position++, offset, offset + 3);
            offset += 4;
            for (int t = 0; t < 1 + rng.Next(6); t++)
            {
                string w = words[rng.Next(words.Length)];
                buffer.AddToken(d, "body", Encoding.UTF8.GetBytes(w), position++, offset, offset + w.Length);
                offset += w.Length + 1;
            }

            buffer.AddToken(d, "tag", Encoding.UTF8.GetBytes(words[rng.Next(4)]), 0);
        }

        var storage = new MemoryStorage();
        using (var writer = new TextSegmentWriter(storage, "s.terms", "s.postings", "s.lengths", docs, Limits))
        {
            buffer.WriteTo(writer);
            writer.Finish();
        }

        return new Baseline(ReadAll(storage, "s.terms"), ReadAll(storage, "s.postings"), ReadAll(storage, "s.lengths"));
    }

    private static byte[] ReadAll(MemoryStorage storage, string name)
    {
        using IRangeReader r = storage.OpenRead(name);
        var bytes = new byte[r.Length];
        r.ReadExactly(0, bytes);
        return bytes;
    }

    public static void WriteSeeds(string directory, bool terms)
    {
        Baseline good = Good.Value;
        File.WriteAllBytes(Path.Combine(directory, "valid"), [1, .. (terms ? good.Terms : good.Postings)]);
    }
}
