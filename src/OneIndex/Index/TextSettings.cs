using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>
/// Typo tolerance settings, with the names and defaults of Meilisearch's documented typo tolerance: one typo from 5 characters, two from
/// 9, never more than two, and a typo on the first character counting as two.
/// </summary>
public sealed class TypoSettings
{
    /// <summary>Whether query words may match indexed words that differ by typos. Default <see langword="true"/>.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The shortest query word, in characters, that may have one typo. Default 5; between 0 and <see cref="MinWordSizeForTwoTypos"/>.</summary>
    public int MinWordSizeForOneTypo { get; init; } = 5;

    /// <summary>The shortest query word, in characters, that may have two typos. Default 9; between <see cref="MinWordSizeForOneTypo"/> and 255.</summary>
    public int MinWordSizeForTwoTypos { get; init; } = 9;

    /// <summary>Whether words made only of digits are matched exactly (or as prefixes). Default <see langword="false"/>.</summary>
    public bool DisableOnNumbers { get; init; }

    /// <summary>Query words that never get typos, compared ignoring case. Default none.</summary>
    public IReadOnlyList<string> DisableOnWords { get; init; } = [];

    /// <summary>Names of fields in which matches never involve typos. Default none.</summary>
    public IReadOnlyList<string> DisableOnFields { get; init; } = [];

    /// <summary>The number of typos a query word of <paramref name="length"/> characters may have under these settings (0 to 2).</summary>
    /// <param name="length">The word length in Unicode code points.</param>
    /// <returns>The budget; 0 when typo tolerance is off.</returns>
    public int Budget(int length) => !Enabled ? 0 : length >= MinWordSizeForTwoTypos ? 2 : length >= MinWordSizeForOneTypo ? 1 : 0;

    internal bool IsDefault => Enabled && MinWordSizeForOneTypo == 5 && MinWordSizeForTwoTypos == 9 && !DisableOnNumbers && DisableOnWords.Count == 0 && DisableOnFields.Count == 0;

    internal void Validate()
    {
        if (MinWordSizeForOneTypo < 0 || MinWordSizeForOneTypo > MinWordSizeForTwoTypos || MinWordSizeForTwoTypos > 255)
        {
            throw new ArgumentException($"Typo tolerance word sizes must satisfy 0 <= one typo ({MinWordSizeForOneTypo}) <= two typos ({MinWordSizeForTwoTypos}) <= 255.");
        }
    }
}

/// <summary>One synonym rule: a group whose members stand for each other, or a word that also matches others one way.</summary>
/// <param name="From">For a group, all its members; for a one-way rule, the single word (or phrase) whose queries are widened.</param>
/// <param name="To">For a one-way rule, the words it also matches; empty for a group.</param>
public sealed record SynonymRule(IReadOnlyList<string> From, IReadOnlyList<string> To)
{
    /// <summary>Whether this is a group (every member matches every other).</summary>
    public bool IsGroup => To.Count == 0;
}

/// <summary>
/// The query-time text settings of an index, recorded in the schema of every commit. They never change what is indexed, so a changed
/// setting applies to the next query without rebuilding anything.
/// </summary>
public sealed class TextSettings
{
    private const int MaxListItems = 100_000;

    /// <summary>The defaults: typo tolerance and as-you-type on, no synonyms, no stop words.</summary>
    public static TextSettings Default { get; } = new();

    /// <summary>Typo tolerance. Default: on, one typo from 5 characters, two from 9.</summary>
    public TypoSettings Typo { get; init; } = new();

    /// <summary>Whether the last word of user text also matches words that begin with it. Default <see langword="true"/>.</summary>
    public bool PrefixSearch { get; init; } = true;

    /// <summary>Synonym rules, applied to user text. Default none.</summary>
    public List<SynonymRule> Synonyms { get; init; } = [];

    /// <summary>Words dropped from user text before matching. Default none.</summary>
    public IReadOnlyList<string> StopWords { get; init; } = [];

    /// <summary>
    /// The weight of the term-proximity bonus that user text with two or more words adds to its BM25F score (docs/scoring.md), 0 to 4.
    /// Default 0.5; 0 turns the bonus off.
    /// </summary>
    public float ProximityWeight { get; init; } = Search.Proximity.DefaultWeight;

    /// <summary>
    /// Ranking rules for user-text searches, in order, as <see cref="Search.RankingRule"/> writes them (<c>words</c>, <c>typo</c>,
    /// <c>proximity</c>, <c>attribute</c>, <c>sort</c>, <c>exactness</c>, <c>field:asc</c>, <c>field:desc</c>). Default <see langword="null"/>:
    /// no rules, results are ordered by BM25F score. An empty list also means no rules.
    /// </summary>
    public IReadOnlyList<string>? RankingRules { get; init; }

    /// <summary>Whether every setting has its default value.</summary>
    public bool IsDefault => IsTextDefault && !HasRankingSettings;

    internal bool IsTextDefault => Typo.IsDefault && PrefixSearch && Synonyms.Count == 0 && StopWords.Count == 0;

    internal bool HasRankingSettings => ProximityWeight != Search.Proximity.DefaultWeight || RankingRules is { Count: > 0 };

    internal void WriteRanking(ByteSink sink)
    {
        sink.WriteUInt32(BitConverter.SingleToUInt32Bits(ProximityWeight));
        WriteList(sink, RankingRules ?? []);
    }

    internal static (float ProximityWeight, IReadOnlyList<string>? Rules) ReadRanking(ref ByteSource src, IndexLimits limits)
    {
        float weight = BitConverter.UInt32BitsToSingle(src.ReadUInt32());
        if (!(weight >= 0f && weight <= 4f))
        {
            throw new MalformedInputException("the proximity weight is out of range");
        }

        List<string> rules = ReadList(ref src, limits);
        try
        {
            _ = Search.RankingRule.ParseList(rules);
        }
        catch (ArgumentException e)
        {
            throw new MalformedInputException(e.Message);
        }

        return (weight, rules.Count == 0 ? null : rules);
    }

    internal void Validate()
    {
        Typo.Validate();
        if (!(ProximityWeight >= 0f && ProximityWeight <= 4f))
        {
            throw new ArgumentException($"The proximity weight is 0 to 4, got {ProximityWeight}.");
        }

        if (RankingRules is { } rules)
        {
            _ = Search.RankingRule.ParseList(rules);
        }
    }

    internal void Write(ByteSink sink)
    {
        sink.WriteByte((byte)((Typo.Enabled ? 0 : 1) | (Typo.DisableOnNumbers ? 2 : 0) | (PrefixSearch ? 0 : 4)));
        sink.WriteVarUInt((ulong)Typo.MinWordSizeForOneTypo);
        sink.WriteVarUInt((ulong)Typo.MinWordSizeForTwoTypos);
        WriteList(sink, Typo.DisableOnWords);
        WriteList(sink, Typo.DisableOnFields);
        WriteList(sink, StopWords);
        sink.WriteVarUInt((ulong)Synonyms.Count);
        foreach (SynonymRule rule in Synonyms)
        {
            WriteList(sink, rule.From);
            WriteList(sink, rule.To);
        }
    }

    internal static TextSettings Read(ref ByteSource src, IndexLimits limits)
    {
        byte flags = src.ReadByte();
        if ((flags & ~7) != 0)
        {
            throw new MalformedInputException("unknown text settings flag");
        }

        int one = src.ReadCount(255, "typo word size");
        int two = src.ReadCount(255, "typo word size");
        var typo = new TypoSettings
        {
            Enabled = (flags & 1) == 0,
            DisableOnNumbers = (flags & 2) != 0,
            MinWordSizeForOneTypo = one,
            MinWordSizeForTwoTypos = two,
            DisableOnWords = ReadList(ref src, limits),
            DisableOnFields = ReadList(ref src, limits),
        };
        IReadOnlyList<string> stop = ReadList(ref src, limits);
        int rules = src.ReadCount(MaxListItems, "synonym rules");
        var synonyms = new List<SynonymRule>(rules);
        for (int i = 0; i < rules; i++)
        {
            IReadOnlyList<string> from = ReadList(ref src, limits);
            synonyms.Add(new SynonymRule(from, ReadList(ref src, limits)));
        }

        var settings = new TextSettings { Typo = typo, PrefixSearch = (flags & 4) == 0, Synonyms = synonyms, StopWords = stop };
        try
        {
            settings.Validate();
        }
        catch (ArgumentException e)
        {
            throw new MalformedInputException(e.Message);
        }

        return settings;
    }

    private static void WriteList(ByteSink sink, IReadOnlyList<string> items)
    {
        sink.WriteVarUInt((ulong)items.Count);
        foreach (string s in items)
        {
            sink.WriteString(s);
        }
    }

    private static List<string> ReadList(ref ByteSource src, IndexLimits limits)
    {
        int n = src.ReadCount(MaxListItems, "text settings list");
        var list = new List<string>(Math.Min(n, 64));
        for (int i = 0; i < n; i++)
        {
            list.Add(src.ReadString(limits.MaxStringBytes, "text settings word"));
        }

        return list;
    }
}
