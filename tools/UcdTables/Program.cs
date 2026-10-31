// Generates OneIndex's Unicode tables from the Unicode Character Database.
//
//   dotnet run --project tools/UcdTables -- <ucd directory> <output directory>
//
// The UCD directory holds the files below (anywhere under it; the layout of the published UCD tree or a flat copy both work):
// WordBreakProperty.txt, SentenceBreakProperty.txt, emoji-data.txt, CaseFolding.txt, UnicodeData.txt, Scripts.txt and
// DerivedNormalizationProps.txt. The output is the file UnicodeTables.g.cs. The header of the generated file records the
// Unicode version and the SHA-256 of every input, so a regeneration can be checked against the pinned UCD.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OneIndex.Tools.UcdTables;

internal static class Program
{
    private const int MaxCodePoint = 0x10FFFF;
    private const int BlockShift = 7;
    private const int BlockSize = 1 << BlockShift;

    // The value orders are part of the generated enums; the segmenters compare against the enum members.
    private static readonly string[] WordBreakNames =
    [
        "Other", "CR", "LF", "Newline", "Extend", "ZWJ", "Regional_Indicator", "Format", "Katakana", "Hebrew_Letter", "ALetter",
        "Single_Quote", "Double_Quote", "MidNumLet", "MidLetter", "MidNum", "Numeric", "ExtendNumLet", "WSegSpace",
    ];

    private static readonly string[] SentenceBreakNames =
    [
        "Other", "CR", "LF", "Extend", "Sep", "Format", "Sp", "Lower", "Upper", "OLetter", "Numeric", "ATerm", "SContinue", "STerm",
        "Close",
    ];

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: UcdTables <ucd directory> <output directory>");
            return 2;
        }

        string ucd = args[0];
        string outDir = args[1];
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string Find(string name)
        {
            string[] hits = Directory.GetFiles(ucd, name, SearchOption.AllDirectories);
            if (hits.Length == 0)
            {
                throw new FileNotFoundException($"{name} not found under {ucd}");
            }

            inputs[name] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(hits[0])));
            return hits[0];
        }

        var sb = new StringBuilder();
        string[] unicodeData = File.ReadAllLines(Find("UnicodeData.txt"));

        // General category and canonical combining class, with the First/Last range convention expanded.
        var gc = new string[MaxCodePoint + 1];
        var ccc = new byte[MaxCodePoint + 1];
        var decompositions = new SortedDictionary<int, (bool Compat, int[] Mapping)>();
        int rangeStart = -1;
        foreach (string line in unicodeData)
        {
            string[] f = line.Split(';');
            int cp = int.Parse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (f[1].EndsWith(", First>", StringComparison.Ordinal))
            {
                rangeStart = cp;
                continue;
            }

            int first = cp;
            if (f[1].EndsWith(", Last>", StringComparison.Ordinal))
            {
                first = rangeStart;
            }

            for (int c = first; c <= cp; c++)
            {
                gc[c] = f[2];
                ccc[c] = byte.Parse(f[3], CultureInfo.InvariantCulture);
            }

            if (f[5].Length > 0)
            {
                bool compat = f[5].StartsWith('<');
                string[] parts = f[5].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int[] mapping = parts.Where(p => !p.StartsWith('<')).Select(p => int.Parse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
                decompositions[cp] = (compat, mapping);
            }
        }

        // Word_Break (bits 0-4), token character (bit 5: letter, decimal digit or letter number), Extended_Pictographic (bit 7).
        var word = new byte[MaxCodePoint + 1];
        foreach ((int lo, int hi, string value) in ReadRanges(Find("WordBreakProperty.txt")))
        {
            int v = Array.IndexOf(WordBreakNames, value);
            Require(v > 0, "unknown Word_Break value " + value);
            for (int c = lo; c <= hi; c++)
            {
                word[c] = (byte)v;
            }
        }

        foreach ((int lo, int hi, string value) in ReadRanges(Find("emoji-data.txt")))
        {
            if (value != "Extended_Pictographic")
            {
                continue;
            }

            for (int c = lo; c <= hi; c++)
            {
                word[c] |= 0x80;
            }
        }

        for (int c = 0; c <= MaxCodePoint; c++)
        {
            string? g = gc[c];
            if (g is not null && (g[0] == 'L' || g == "Nd" || g == "Nl"))
            {
                word[c] |= 0x20;
            }
        }

        var sentence = new byte[MaxCodePoint + 1];
        foreach ((int lo, int hi, string value) in ReadRanges(Find("SentenceBreakProperty.txt")))
        {
            int v = Array.IndexOf(SentenceBreakNames, value);
            Require(v > 0, "unknown Sentence_Break value " + value);
            for (int c = lo; c <= hi; c++)
            {
                sentence[c] = (byte)v;
            }
        }

        // Script class for the CJK bigram tokenizer: 1 Han, 2 Hiragana, 3 Katakana, 4 Hangul.
        var script = new byte[MaxCodePoint + 1];
        foreach ((int lo, int hi, string value) in ReadRanges(Find("Scripts.txt")))
        {
            byte v = value switch { "Han" => 1, "Hiragana" => 2, "Katakana" => 3, "Hangul" => 4, _ => 0 };
            if (v == 0)
            {
                continue;
            }

            for (int c = lo; c <= hi; c++)
            {
                script[c] = v;
            }
        }

        // The prolonged sound marks belong to Katakana text (and are Script=Common), so a katakana word keeps them.
        script[0x30FC] = 3;
        script[0xFF70] = 3;

        // Full case folding: status C (common) and F (full). S (simple) and T (Turkic) are not used.
        var folds = new SortedDictionary<int, string>();
        foreach (string raw in File.ReadAllLines(Find("CaseFolding.txt")))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] f = line.Split(';', StringSplitOptions.TrimEntries);
            if (f[1] is not ("C" or "F"))
            {
                continue;
            }

            int cp = int.Parse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var chars = new StringBuilder();
            foreach (string p in f[2].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                chars.Append(char.ConvertFromUtf32(int.Parse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }

            folds[cp] = chars.ToString();
        }

        // Normalization: fully expanded decompositions, composition pairs.
        var excluded = new HashSet<int>();
        foreach ((int lo, int hi, string value) in ReadRanges(Find("DerivedNormalizationProps.txt")))
        {
            if (value == "Full_Composition_Exclusion")
            {
                for (int c = lo; c <= hi; c++)
                {
                    excluded.Add(c);
                }
            }
        }

        string Expand(int cp, bool compat)
        {
            if (decompositions.TryGetValue(cp, out (bool Compat, int[] Mapping) d) && (compat || !d.Compat))
            {
                return string.Concat(d.Mapping.Select(m => Expand(m, compat)));
            }

            return char.ConvertFromUtf32(cp);
        }

        var canonical = new SortedDictionary<int, string>();
        var compatible = new SortedDictionary<int, string>();
        var pairs = new SortedDictionary<long, int>();
        foreach ((int cp, (bool Compat, int[] Mapping) d) in decompositions)
        {
            if (!d.Compat)
            {
                canonical[cp] = Expand(cp, compat: false);
                if (d.Mapping.Length == 2 && !excluded.Contains(cp))
                {
                    pairs[((long)d.Mapping[0] << 21) | (uint)d.Mapping[1]] = cp;
                }
            }

            compatible[cp] = Expand(cp, compat: true);
        }

        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Generated by tools/UcdTables from the Unicode Character Database, version 17.0.0. Do not edit.");
        sb.AppendLine("// Inputs (SHA-256):");
        foreach ((string name, string hash) in inputs)
        {
            sb.AppendLine($"//   {name} {hash}");
        }

        sb.AppendLine("// Unicode data files are used under the Unicode License v3 (https://www.unicode.org/license.txt);");
        sb.AppendLine("// see THIRD-PARTY-NOTICES.md.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace OneIndex.Analysis.Unicode;");
        sb.AppendLine();
        EmitEnum(sb, "WordBreak", WordBreakNames);
        EmitEnum(sb, "SentenceBreak", SentenceBreakNames);
        sb.AppendLine("/// <summary>Generated property tables. Two-stage tries index a code point's block, then its entry within the block.</summary>");
        sb.AppendLine("internal static class UnicodeTables");
        sb.AppendLine("{");
        sb.AppendLine($"    internal const int BlockShift = {BlockShift};");
        sb.AppendLine();
        EmitTrie(sb, "Word", word);
        EmitTrie(sb, "Sentence", sentence);
        EmitTrie(sb, "Script", script);
        EmitTrie(sb, "Ccc", ccc);

        EmitMap(sb, "CaseFold", folds.ToDictionary(k => k.Key, k => k.Value));
        EmitMap(sb, "Canonical", canonical.ToDictionary(k => k.Key, k => k.Value));
        EmitMap(sb, "Compatible", compatible.ToDictionary(k => k.Key, k => k.Value));

        EmitSpan(sb, "long", "ComposeKeys", pairs.Keys.Select(k => k.ToString(CultureInfo.InvariantCulture) + "L"));
        EmitSpan(sb, "int", "ComposeValues", pairs.Values.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        sb.AppendLine("}");

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "UnicodeTables.g.cs"), sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
        Console.WriteLine($"wrote UnicodeTables.g.cs: {folds.Count} folds, {canonical.Count} canonical, {compatible.Count} compatible, {pairs.Count} pairs");
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    private static IEnumerable<(int Lo, int Hi, string Value)> ReadRanges(string path)
    {
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] f = line.Split(';', StringSplitOptions.TrimEntries);
            string[] range = f[0].Split("..", StringSplitOptions.None);
            int lo = int.Parse(range[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int hi = range.Length > 1 ? int.Parse(range[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : lo;
            yield return (lo, hi, f[1]);
        }
    }

    private static void EmitEnum(StringBuilder sb, string name, string[] values)
    {
        sb.AppendLine($"/// <summary>The {name} property values, in the order of the generated tables.</summary>");
        sb.AppendLine($"internal enum {name} : byte");
        sb.AppendLine("{");
        for (int i = 0; i < values.Length; i++)
        {
            sb.AppendLine($"    {values[i].Replace("_", string.Empty, StringComparison.Ordinal)} = {i},");
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitTrie(StringBuilder sb, string name, byte[] table)
    {
        int blocks = (MaxCodePoint + 1) / BlockSize;
        var index = new ushort[blocks];
        var data = new List<byte>();
        var seen = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (int b = 0; b < blocks; b++)
        {
            var block = new ReadOnlySpan<byte>(table, b * BlockSize, BlockSize);
            string key = Convert.ToBase64String(block);
            if (!seen.TryGetValue(key, out ushort at))
            {
                at = (ushort)(data.Count / BlockSize);
                seen[key] = at;
                data.AddRange(block.ToArray());
            }

            index[b] = at;
        }

        EmitSpan(sb, "ushort", name + "Stage1", index.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        EmitSpan(sb, "byte", name + "Stage2", data.Select(i => i.ToString(CultureInfo.InvariantCulture)));
    }

    // A map from code point to a UTF-16 string: sorted keys, packed (offset << 5 | length) infos, one character pool.
    private static void EmitMap(StringBuilder sb, string name, Dictionary<int, string> map)
    {
        var pool = new StringBuilder();
        var keys = new List<int>();
        var infos = new List<int>();
        foreach ((int key, string value) in map.OrderBy(k => k.Key))
        {
            Require(value.Length < 32, "mapping too long");
            keys.Add(key);
            infos.Add((pool.Length << 5) | value.Length);
            pool.Append(value);
        }

        EmitSpan(sb, "int", name + "Keys", keys.Select(k => k.ToString(CultureInfo.InvariantCulture)));
        EmitSpan(sb, "int", name + "Infos", infos.Select(k => k.ToString(CultureInfo.InvariantCulture)));
        EmitSpan(sb, "char", name + "Pool", pool.ToString().Select(c => $"'\\u{(int)c:X4}'"));
    }

    private static void EmitSpan(StringBuilder sb, string type, string name, IEnumerable<string> items)
    {
        // Arrays, not ReadOnlySpan properties: in builds without optimization the compiler allocates a new array on every
        // property read, and a lookup must not allocate.
        sb.AppendLine($"    internal static readonly {type}[] {name} =");
        sb.Append("    [");
        int n = 0;
        foreach (string item in items)
        {
            if (n % 16 == 0)
            {
                sb.Append("\n        ");
            }

            sb.Append(item).Append(", ");
            n++;
        }

        sb.AppendLine("\n    ];");
        sb.AppendLine();
    }
}
