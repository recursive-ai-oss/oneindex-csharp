using System.Globalization;
using System.Text;
using OneIndex.Analysis;
using OneIndex.Analysis.Asian;

namespace OneIndex.Dictionaries.Tool;

internal static class Program
{
    private static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            switch (args.Length > 0 ? args[0] : string.Empty)
            {
                case "build-mecab":
                {
                    // build-mecab <ja|ko> <dir> <out.oidict>
                    bool ko = args[1] == "ko";
                    Func<string, bool> include = ko
                        ? name => name is not ("Person.csv" or "Person-actor.csv" or "Place-address.csv")
                        : _ => true;
                    byte[] bytes = Builders.BuildMeCab(args[2], ko, ko ? Encoding.UTF8 : Encoding.GetEncoding("euc-jp"), include);
                    File.WriteAllBytes(args[3], bytes);
                    Console.WriteLine($"{args[3]}: {bytes.Length:N0} bytes");
                    return 0;
                }

                case "build-jieba":
                {
                    byte[] bytes = Builders.BuildJieba(args[1]);
                    File.WriteAllBytes(args[2], bytes);
                    Console.WriteLine($"{args[2]}: {bytes.Length:N0} bytes");
                    return 0;
                }

                case "build-wordlist":
                {
                    byte[] bytes = Builders.BuildWordList(args[1]);
                    File.WriteAllBytes(args[2], bytes);
                    Console.WriteLine($"{args[2]}: {bytes.Length:N0} bytes");
                    return 0;
                }

                case "analyze":
                {
                    // analyze <ja|ko|zh|th|lo|km|my> <dict.oidict> <text>
                    byte[] dict = File.ReadAllBytes(args[2]);
                    Analyzer analyzer = args[1] switch
                    {
                        "ja" => new JapaneseAnalyzer(LatticeLexicon.Load(dict)),
                        "ko" => new KoreanAnalyzer(LatticeLexicon.Load(dict)),
                        "zh" => new ChineseAnalyzer(WordList.Load(dict)),
                        "th" => new WordBreakAnalyzer(WordBreakScript.Thai, WordList.Load(dict)),
                        "lo" => new WordBreakAnalyzer(WordBreakScript.Lao, WordList.Load(dict)),
                        "km" => new WordBreakAnalyzer(WordBreakScript.Khmer, WordList.Load(dict)),
                        _ => new WordBreakAnalyzer(WordBreakScript.Myanmar, WordList.Load(dict)),
                    };
                    foreach (AnalyzedToken t in analyzer.Analyze(args[3]))
                    {
                        Console.WriteLine($"{t.Term}\tpos={t.Position} [{t.StartOffset},{t.EndOffset})");
                    }

                    return 0;
                }

                case "eval":
                    return Evaluate.Run(args);

                case "speed":
                    return Evaluate.Speed(args);

                default:
                    Console.Error.WriteLine("usage: build-mecab <ja|ko> <dir> <out> | build-jieba <dict.txt> <out> | build-wordlist <list.txt> <out> | eval <lang> <dict> <conllu> | speed <lang> <dict> <textfile>");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or DictionaryFormatException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

/// <summary>Word-segmentation scoring against CoNLL-U treebanks and a throughput measurement.</summary>
internal static class Evaluate
{
    internal static int Run(string[] args)
    {
        // eval <ja|zh|th|ko> <dict.oidict> <file.conllu> [normal]
        string lang = args[1];
        byte[] dict = File.ReadAllBytes(args[2]);
        bool normal = args.Length > 4 && args[4] == "normal";
        Func<string, List<(int, int)>> segment = Segmenter(lang, dict, normal);
        long gold = 0, pred = 0, hit = 0;
        int sentences = 0;
        var shown = 0;
        foreach ((string text, List<(int, int)> goldSpans) in ReadConllu(args[3]))
        {
            List<(int, int)> p = segment(text);
            var set = new HashSet<(int, int)>(goldSpans);
            int h = p.Count(set.Contains);
            gold += goldSpans.Count;
            pred += p.Count;
            hit += h;
            sentences++;
            if (h < goldSpans.Count && shown < 8 && Environment.GetEnvironmentVariable("SHOW_DIFF") == "1")
            {
                shown++;
                Console.WriteLine("gold: " + string.Join(" ", goldSpans.Select(s => text.Substring(s.Item1, s.Item2 - s.Item1))));
                Console.WriteLine("pred: " + string.Join(" ", p.Select(s => text.Substring(s.Item1, s.Item2 - s.Item1))));
            }
        }

        double pr = pred == 0 ? 0 : (double)hit / pred, rc = gold == 0 ? 0 : (double)hit / gold, f = pr + rc == 0 ? 0 : 2 * pr * rc / (pr + rc);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{lang} sentences={sentences} gold={gold} predicted={pred} correct={hit} P={pr:F4} R={rc:F4} F1={f:F4}"));
        return 0;
    }

    internal static int Speed(string[] args)
    {
        string lang = args[1];
        Func<string, List<(int, int)>> segment = Segmenter(lang, File.ReadAllBytes(args[2]), false);
        string[] lines = File.ReadAllLines(args[3]).Where(l => l.Length > 0).ToArray();
        long chars = 0;
        foreach (string l in lines.Take(200))
        {
            segment(l); // warm up
        }

        // Repeat the text until it is a few million characters, so a short sample still gives a stable time.
        int repeats = (int)Math.Max(1, 3_000_000 / Math.Max(1, lines.Sum(l => (long)l.Length)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int r = 0; r < repeats; r++)
        {
            foreach (string l in lines)
            {
                segment(l);
                chars += l.Length;
            }
        }

        sw.Stop();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{lang}: {chars:N0} chars in {sw.Elapsed.TotalSeconds:F2}s = {chars * 2 / 1e6 / sw.Elapsed.TotalSeconds:F2} MB/s (UTF-16) , {chars / 1e6 / sw.Elapsed.TotalSeconds:F2} Mchars/s"));
        return 0;
    }

    private static Func<string, List<(int, int)>> Segmenter(string lang, byte[] dict, bool normal)
    {
        var list = new List<Segment>();
        if (lang is "ja" or "ko")
        {
            var seg = new LatticeSegmenter(LatticeLexicon.Load(dict), normal || lang == "ko" ? LatticeOptions.Normal : LatticeOptions.Search);
            return text =>
            {
                var result = new List<(int, int)>();
                int i = 0;
                while (i < text.Length)
                {
                    if (char.IsWhiteSpace(text[i]))
                    {
                        i++;
                        continue;
                    }

                    int j = i;
                    while (j < text.Length && !char.IsWhiteSpace(text[j]))
                    {
                        j++;
                    }

                    seg.Segment(text.AsSpan(i, j - i), list);
                    result.AddRange(list.Select(s => (i + s.Start, i + s.Start + s.Length)));
                    i = j;
                }

                return result;
            };
        }

        WordList wl = WordList.Load(dict);
        var uni = new UnigramSegmenter(wl, searchMode: false);
        var mm = new MaximalMatchSegmenter(wl);
        return text =>
        {
            var result = new List<(int, int)>();
            int i = 0;
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    i++;
                    continue;
                }

                int j = i + 1;
                bool target = IsScript(lang, text[i]);
                while (j < text.Length && !char.IsWhiteSpace(text[j]) && IsScript(lang, text[j]) == target && (target || SameKind(text[i], text[j])))
                {
                    j++;
                }

                if (!target)
                {
                    result.Add((i, j));
                }
                else
                {
                    if (lang == "zh")
                    {
                        uni.Segment(text.AsSpan(i, j - i), list);
                    }
                    else
                    {
                        mm.Segment(text.AsSpan(i, j - i), list);
                    }

                    result.AddRange(list.Where(s => !s.Stacked).Select(s => (i + s.Start, i + s.Start + s.Length)));
                }

                i = j;
            }

            return result;
        };
    }

    private static bool IsScript(string lang, char c) => lang switch
    {
        "zh" => c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF,
        "th" => c >= 0x0E01 && c <= 0x0E7F,
        "lo" => c >= 0x0E81 && c <= 0x0EDF,
        "km" => c >= 0x1780 && c <= 0x17FF,
        _ => c >= 0x1000 && c <= 0x109F,
    };

    // Outside the script, a run of letters or digits is one token and every other character stands alone.
    private static bool SameKind(char a, char b) => char.IsLetterOrDigit(a) && char.IsLetterOrDigit(b);

    private static IEnumerable<(string Text, List<(int, int)> Words)> ReadConllu(string path)
    {
        var sb = new StringBuilder();
        var spans = new List<(int, int)>();
        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0)
            {
                if (sb.Length > 0)
                {
                    yield return (sb.ToString(), spans);
                }

                sb.Clear();
                spans = [];
                continue;
            }

            if (line[0] == '#')
            {
                continue;
            }

            string[] f = line.Split('\t');
            if (f.Length < 10 || f[0].Contains('-', StringComparison.Ordinal) || f[0].Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            int start = sb.Length;
            sb.Append(f[1]);
            spans.Add((start, sb.Length));
            if (!f[9].Contains("SpaceAfter=No", StringComparison.Ordinal) && Environment.GetEnvironmentVariable("EVAL_SPACES") == "1")
            {
                sb.Append(' ');
            }
        }

        if (sb.Length > 0)
        {
            yield return (sb.ToString(), spans);
        }
    }
}
