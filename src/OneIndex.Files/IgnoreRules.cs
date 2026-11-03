namespace OneIndex.Files;

/// <summary>
/// Ignore rules in the <c>.gitignore</c> style: one pattern per line, <c>#</c> comments, <c>!</c> to re-include, a trailing <c>/</c> for
/// directories only, a leading or inner <c>/</c> to anchor a pattern to the directory of the rules file, <c>*</c>, <c>?</c>, <c>[a-z]</c> within a
/// name and <c>**</c> across directories. The last matching rule wins. As in git, a file inside an ignored directory cannot be re-included.
/// </summary>
public sealed class IgnoreRules
{
    private readonly List<Rule> _rules = [];

    /// <summary>The rules every folder index applies unless turned off: version-control directories, dependency folders, editor and system litter.</summary>
    public static IgnoreRules Defaults { get; } = Parse(
        ".git/\n.svn/\n.hg/\nnode_modules/\n__pycache__/\n.DS_Store\nThumbs.db\n~$*\n*.tmp\n*.swp\n*~\n");

    /// <summary>Parses the lines of a rules file.</summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The rules.</returns>
    public static IgnoreRules Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rules = new IgnoreRules();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            // Trailing spaces are dropped unless escaped.
            int end = line.Length;
            while (end > 0 && line[end - 1] == ' ' && !(end > 1 && line[end - 2] == '\\'))
            {
                end--;
            }

            line = line[..end];
            if (line.Length == 0)
            {
                continue;
            }

            bool negate = false;
            if (line[0] == '!')
            {
                negate = true;
                line = line[1..];
            }
            else if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal))
            {
                line = line[1..];
            }

            bool dirOnly = line.EndsWith('/');
            line = line.TrimEnd('/');
            if (line.Length == 0)
            {
                continue;
            }

            bool anchored = line.Contains('/');
            line = line.TrimStart('/');
            rules._rules.Add(new Rule(line.Split('/'), negate, dirOnly, anchored));
        }

        return rules;
    }

    /// <summary>The number of rules.</summary>
    public int Count => _rules.Count;

    /// <summary>Decides whether an entry is ignored by these rules alone.</summary>
    /// <param name="relativePath">The entry's path relative to the directory of the rules, with <c>/</c> separators.</param>
    /// <param name="isDirectory">Whether the entry is a directory.</param>
    /// <param name="previous">The verdict so far (from rules read earlier): null for no verdict.</param>
    /// <returns>True for ignored, false for re-included, null when no rule matches (the previous verdict stands).</returns>
    public bool? Evaluate(string relativePath, bool isDirectory, bool? previous = null)
    {
        bool? verdict = previous;
        string[] segments = relativePath.Split('/');
        foreach (Rule r in _rules)
        {
            if (r.DirOnly && !isDirectory)
            {
                continue;
            }

            bool hit = r.Anchored
                ? MatchSegments(r.Segments, 0, segments, 0)
                : MatchName(r.Segments[0], segments[^1]);
            if (hit)
            {
                verdict = !r.Negate;
            }
        }

        return verdict;
    }

    private static bool MatchSegments(string[] pattern, int p, string[] path, int s)
    {
        while (p < pattern.Length)
        {
            if (pattern[p] == "**")
            {
                if (p == pattern.Length - 1)
                {
                    return s < path.Length; // a trailing ** matches everything inside
                }

                for (int k = s; k <= path.Length; k++)
                {
                    if (MatchSegments(pattern, p + 1, path, k))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (s >= path.Length || !MatchName(pattern[p], path[s]))
            {
                return false;
            }

            p++;
            s++;
        }

        return s == path.Length;
    }

    /// <summary>Matches one path segment against a glob of <c>*</c>, <c>?</c>, <c>[set]</c> and backslash escapes.</summary>
    internal static bool MatchName(string glob, string name)
    {
        int g = 0;
        int n = 0;
        int starG = -1;
        int starN = 0;
        while (n < name.Length)
        {
            if (g < glob.Length && glob[g] == '*')
            {
                starG = g++;
                starN = n;
                continue;
            }

            if (g < glob.Length && TryMatchOne(glob, ref g, name[n]))
            {
                n++;
                continue;
            }

            if (starG >= 0)
            {
                g = starG + 1;
                n = ++starN;
                continue;
            }

            return false;
        }

        while (g < glob.Length && glob[g] == '*')
        {
            g++;
        }

        return g == glob.Length;
    }

    // Matches the glob element at g against c and advances g past it when it matches.
    private static bool TryMatchOne(string glob, ref int g, char c)
    {
        char x = glob[g];
        if (x == '?')
        {
            g++;
            return true;
        }

        if (x == '\\' && g + 1 < glob.Length)
        {
            if (glob[g + 1] != c)
            {
                return false;
            }

            g += 2;
            return true;
        }

        if (x == '[')
        {
            int close = glob.IndexOf(']', g + 2 < glob.Length ? g + 2 : glob.Length);
            if (close < 0)
            {
                if (c != '[')
                {
                    return false;
                }

                g++;
                return true;
            }

            int i = g + 1;
            bool negate = glob[i] is '!' or '^';
            if (negate)
            {
                i++;
            }

            bool matched = false;
            while (i < close)
            {
                if (i + 2 < close && glob[i + 1] == '-')
                {
                    matched |= c >= glob[i] && c <= glob[i + 2];
                    i += 3;
                }
                else
                {
                    matched |= c == glob[i];
                    i++;
                }
            }

            if (matched == negate)
            {
                return false;
            }

            g = close + 1;
            return true;
        }

        if (x != c)
        {
            return false;
        }

        g++;
        return true;
    }

    private sealed record Rule(string[] Segments, bool Negate, bool DirOnly, bool Anchored);
}
