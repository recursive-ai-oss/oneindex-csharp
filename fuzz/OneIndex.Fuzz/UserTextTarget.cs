using OneIndex.Queries;

namespace OneIndex.Fuzz;

/// <summary>A small document type for the user-text target: a stemmed body, a plain title, a keyword and a number.</summary>
public class FuzzDocument
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText(Boost = 2)]
    public string Title { get; set; } = string.Empty;

    [FullText(Language = "en")]
    public string Body { get; set; } = string.Empty;

    [Keyword]
    public string Tag { get; set; } = string.Empty;

    [Filterable]
    public int Number { get; set; }
}

/// <summary>
/// Feeds arbitrary UTF-16 text (lone surrogates and control characters included) to the user-text parser and to a search over a
/// small index, with and without a filter. Nothing may be thrown, and a search must return a bounded, finite-scored page.
/// </summary>
internal static class UserTextTarget
{
    private static readonly SearchIndex<FuzzDocument> Index = Build();

    public static void Run(ReadOnlySpan<byte> data)
    {
        var chars = new char[data.Length / 2];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)(data[2 * i] | (data[(2 * i) + 1] << 8));
        }

        string text = new(chars);
        UserText parsed = UserText.Parse(text);
        Require(parsed.Required.Count + parsed.Excluded.Count <= UserText.MaxOperands, "operand cap");
        foreach (IReadOnlyList<UserTextOperand> group in parsed.Required)
        {
            Require(group.Count > 0, "empty group");
            foreach (UserTextOperand o in group)
            {
                Require(o.Text.Length <= UserText.MaxLength && !o.Text.Contains('\0'), "operand text");
            }
        }

        SearchResults<FuzzDocument> page = Index.Search(text).Take(5).Execute();
        Require(page.Count <= 5 && page.TotalHits >= page.Count, "page");
        foreach (SearchHit<FuzzDocument> hit in page)
        {
            Require(float.IsFinite(hit.Score), "score");
            _ = hit.Highlights;
        }

        int bound = chars.Length % 7;
        _ = Index.Search(text).Where(x => x.Number > bound).Skip(1).Take(3).CountExactly().Count();
    }

    public static void WriteSeeds(string directory)
    {
        string[] seeds = ["overdue \"acme corp\" -draft", "cat OR dog", "\"unterminated", "-", "state-of-the-art", "", "\ud83d\ude00 \ud800 x"];
        for (int i = 0; i < seeds.Length; i++)
        {
            byte[] bytes = new byte[seeds[i].Length * 2];
            for (int c = 0; c < seeds[i].Length; c++)
            {
                bytes[2 * c] = (byte)seeds[i][c];
                bytes[(2 * c) + 1] = (byte)(seeds[i][c] >> 8);
            }

            File.WriteAllBytes(Path.Combine(directory, "seed-" + i), bytes);
        }
    }

    private static SearchIndex<FuzzDocument> Build()
    {
        SearchIndex<FuzzDocument> index = SearchIndex.InMemory<FuzzDocument>();
        string[] words = ["overdue", "acme", "corp", "draft", "cat", "dog", "state", "art", "running", "runner", "the", "of", "\u6771\u4eac\u90fd", "caf\u00e9"];
        for (int i = 0; i < 40; i++)
        {
            index.Upsert(new FuzzDocument
            {
                Id = "d" + i,
                Title = words[i % words.Length] + " " + words[(i * 3) % words.Length],
                Body = string.Join(' ', Enumerable.Range(0, 8).Select(k => words[(i + (k * 5)) % words.Length])),
                Tag = words[i % 4],
                Number = i,
            });
        }

        index.Commit();
        return index;
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("user text invariant failed: " + what);
        }
    }
}
