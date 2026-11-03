using System.Text;
using OneIndex.Analysis.Asian;

namespace OneIndex.Dictionaries.Tool;

/// <summary>Reads the published MeCab-format source dictionaries and writes OneIndex dictionary files.</summary>
internal static class Builders
{
    internal static byte[] BuildMeCab(string dir, bool korean, Encoding encoding, Func<string, bool> includeFile)
    {
        var b = new LatticeLexiconBuilder();
        // char.def: classes first, then code point ranges.
        foreach (string raw in File.ReadLines(Path.Combine(dir, "char.def"), encoding))
        {
            string line = StripComment(raw).Trim();
            if (line.Length == 0 || line.StartsWith("0x", StringComparison.Ordinal))
            {
                continue;
            }

            string[] f = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            b.AddClass(f[0], f[1] == "1", f[2] == "1", int.Parse(f[3]));
        }

        foreach (string raw in File.ReadLines(Path.Combine(dir, "char.def"), encoding))
        {
            string line = StripComment(raw).Trim();
            if (!line.StartsWith("0x", StringComparison.Ordinal))
            {
                continue;
            }

            string[] f = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            string range = f[0];
            int dots = range.IndexOf("..", StringComparison.Ordinal);
            int start = Convert.ToInt32(dots < 0 ? range : range[..dots], 16);
            int end = dots < 0 ? start : Convert.ToInt32(range[(dots + 2)..], 16);
            // Only the first class names the character; further names are "compatible" classes, which this reader leaves out.
            b.MapRange(start, end, f[1]);
        }

        foreach (string raw in File.ReadLines(Path.Combine(dir, "unk.def"), encoding))
        {
            string[] f = SplitCsv(raw);
            if (f.Length < 5)
            {
                continue;
            }

            b.AddUnknown(f[0], int.Parse(f[1]), int.Parse(f[2]), int.Parse(f[3]), korean ? f[4] : string.Join(',', f.Skip(4).Take(4)));
        }

        int words = 0;
        foreach (string file in Directory.GetFiles(dir, "*.csv").Order(StringComparer.Ordinal))
        {
            if (!includeFile(Path.GetFileName(file)))
            {
                continue;
            }

            foreach (string raw in File.ReadLines(file, encoding))
            {
                string[] f = SplitCsv(raw);
                if (f.Length < 11)
                {
                    continue;
                }

                string surface = f[0];
                string pos, extra;
                if (korean)
                {
                    pos = f[4];
                    bool decomposed = f[8] is "Inflect" or "Compound" or "Preanalysis";
                    extra = decomposed ? f[11] : string.Empty;
                }
                else
                {
                    pos = string.Join(',', f.Skip(4).Take(4));
                    extra = f[10] != "*" && f[10] != surface ? f[10] : string.Empty;
                }

                b.AddWord(surface, int.Parse(f[1]), int.Parse(f[2]), int.Parse(f[3]), pos, extra);
                words++;
            }
        }

        string[] head = File.ReadLines(Path.Combine(dir, "matrix.def")).First().Split(' ');
        int rightSize = int.Parse(head[0]), leftSize = int.Parse(head[1]);
        short[] matrix = new short[rightSize * leftSize];
        bool first = true;
        foreach (string line in File.ReadLines(Path.Combine(dir, "matrix.def")))
        {
            if (first)
            {
                first = false;
                continue;
            }

            ReadOnlySpan<char> s = line;
            int a = s.IndexOf(' ');
            int c = s[(a + 1)..].IndexOf(' ') + a + 1;
            int r = int.Parse(s[..a]), l = int.Parse(s[(a + 1)..c]);
            matrix[r * leftSize + l] = short.Parse(s[(c + 1)..]);
        }

        b.SetMatrix(rightSize, leftSize, matrix);
        Console.Error.WriteLine($"{words} words, matrix {rightSize}x{leftSize}");
        return b.Build();
    }

    internal static byte[] BuildJieba(string path)
    {
        var words = new List<(string, uint)>();
        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 2 && uint.TryParse(f[1], out uint freq))
            {
                words.Add((f[0].TrimStart('\uFEFF'), freq));
            }
        }

        return WordList.Build(words);
    }

    internal static byte[] BuildWordList(string path)
    {
        var words = new List<(string, uint)>();
        foreach (string raw in File.ReadLines(path, Encoding.UTF8))
        {
            string line = raw.Trim('\uFEFF', ' ', '\t', '\r');
            if (line.Length == 0 || line[0] == '#' || line.Any(char.IsWhiteSpace))
            {
                continue;
            }

            words.Add((line, 0));
        }

        return WordList.Build(words);
    }

    private static string StripComment(string s)
    {
        int i = s.IndexOf('#', StringComparison.Ordinal);
        return i < 0 ? s : s[..i];
    }

    /// <summary>Splits one line of a MeCab CSV file; a field in double quotes may contain commas.</summary>
    internal static string[] SplitCsv(string line)
    {
        var r = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"' && sb.Length == 0)
            {
                quoted = true;
            }
            else if (c == ',')
            {
                r.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        r.Add(sb.ToString());
        return [.. r];
    }
}
