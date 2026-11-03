using System.Buffers.Binary;
using System.Text;

namespace OneIndex.Analysis.Asian;

/// <summary>One character class of a lattice dictionary (the MeCab-style "char.def" categories).</summary>
/// <param name="Name">The class name, for example <c>KANJI</c>.</param>
/// <param name="Invoke">Whether unknown-word candidates are generated at a position even when the dictionary has words there.</param>
/// <param name="Group">Whether a run of characters of this class forms one candidate unknown word.</param>
/// <param name="Length">Also generate unknown candidates of 1 to this many characters (0 for none).</param>
public sealed record CharacterClass(string Name, bool Invoke, bool Group, int Length);

/// <summary>
/// A dictionary for lattice segmentation (Kudo, Yamamoto and Matsumoto 2004, "Applying Conditional Random Fields to Japanese
/// Morphological Analysis", section 2): words with a left and a right context id and a word cost, a matrix of connection costs
/// between the right id of one word and the left id of the next, and character classes that say how to build unknown words.
/// Words are kept sorted by their UTF-16 code units, so a common-prefix search narrows a range of entries one character at a time.
/// Instances are immutable and thread-safe.
/// </summary>
public sealed class LatticeLexicon
{
    internal const uint Kind = 1;
    internal const uint SecWords = 1, SecOffsets = 2, SecLeft = 3, SecRight = 4, SecCost = 5, SecPos = 6, SecExtra = 7, SecPosTable = 8,
        SecPool = 9, SecMatrix = 10, SecClasses = 11, SecCharTable = 12, SecUnknown = 13, SecInfo = 14;

    private readonly char[] _chars;
    private readonly int[] _offsets;
    private readonly ushort[] _left;
    private readonly ushort[] _right;
    private readonly short[] _cost;
    private readonly ushort[] _pos;
    private readonly int[] _extra;
    private readonly string[] _posTable;
    private readonly char[] _poolChars;
    private readonly int[] _poolOffsets;
    private readonly string?[] _poolCache;
    private readonly short[] _matrix;
    private readonly int _leftSize;
    private readonly byte[] _charTable;
    private readonly (int Start, int End, byte Class)[] _wideRanges;
    private readonly UnknownEntry[][] _unknown;
    private readonly int _maxWordLength;

    internal readonly record struct UnknownEntry(ushort Left, ushort Right, short Cost, ushort Pos);

    private LatticeLexicon(Dictionary<uint, byte[]> s, DictionaryLimits limits)
    {
        byte[] chars = s[SecWords];
        _chars = new char[chars.Length / 2];
        for (int i = 0; i < _chars.Length; i++)
        {
            _chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(chars.AsSpan(i * 2));
        }

        int[] offsets = ReadInts(s[SecOffsets]);
        if (offsets.Length < 1 || offsets.Length - 1 > limits.MaxEntries)
        {
            throw new DictionaryFormatException("The word count is invalid or above the limit.");
        }

        int n = offsets.Length - 1;
        _offsets = offsets;
        int prev = 0;
        foreach (int o in offsets)
        {
            if (o < prev || o > _chars.Length)
            {
                throw new DictionaryFormatException("Word offsets are not increasing or run past the character data.");
            }

            prev = o;
        }

        if (offsets[0] != 0 || offsets[n] != _chars.Length)
        {
            throw new DictionaryFormatException("Word offsets do not cover the character data.");
        }

        DictionaryContainer.RequireSortedWords(_chars, offsets);

        _left = ReadUShorts(s[SecLeft], n);
        _right = ReadUShorts(s[SecRight], n);
        _pos = ReadUShorts(s[SecPos], n);
        byte[] cost = s[SecCost];
        if (cost.Length != n * 2)
        {
            throw new DictionaryFormatException("The cost column has the wrong length.");
        }

        _cost = new short[n];
        for (int i = 0; i < n; i++)
        {
            _cost[i] = BinaryPrimitives.ReadInt16LittleEndian(cost.AsSpan(i * 2));
        }

        _extra = ReadInts(s[SecExtra]);
        if (_extra.Length != n)
        {
            throw new DictionaryFormatException("The extra column has the wrong length.");
        }

        _posTable = ReadStrings(s[SecPosTable]);
        (_poolChars, _poolOffsets) = ReadPool(s[SecPool]);
        _poolCache = new string?[_poolOffsets.Length - 1];
        for (int i = 0; i < n; i++)
        {
            if (_pos[i] >= _posTable.Length)
            {
                throw new DictionaryFormatException("A word names a part of speech that does not exist.");
            }

            if (_extra[i] >= _poolCache.Length)
            {
                throw new DictionaryFormatException("A word names a pool string that does not exist.");
            }
        }

        byte[] m = s[SecMatrix];
        if (m.Length < 4)
        {
            throw new DictionaryFormatException("The connection matrix is truncated.");
        }

        int rightSize = BinaryPrimitives.ReadUInt16LittleEndian(m);
        _leftSize = BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(2));
        if (rightSize == 0 || _leftSize == 0 || m.Length != 4 + rightSize * _leftSize * 2)
        {
            throw new DictionaryFormatException("The connection matrix has the wrong size.");
        }

        _matrix = new short[rightSize * _leftSize];
        for (int i = 0; i < _matrix.Length; i++)
        {
            _matrix[i] = BinaryPrimitives.ReadInt16LittleEndian(m.AsSpan(4 + i * 2));
        }

        for (int i = 0; i < n; i++)
        {
            if (_left[i] >= _leftSize || _right[i] >= rightSize)
            {
                throw new DictionaryFormatException("A word has a context id outside the connection matrix.");
            }
        }

        RightSize = rightSize;
        Classes = ReadClasses(s[SecClasses]);
        byte[] table = s[SecCharTable];
        if (table.Length < 65536 + 4)
        {
            throw new DictionaryFormatException("The character table is truncated.");
        }

        _charTable = table.AsSpan(0, 65536).ToArray();
        foreach (byte c in _charTable)
        {
            if (c >= Classes.Count)
            {
                throw new DictionaryFormatException("The character table names a class that does not exist.");
            }
        }

        int wide = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(65536));
        if (wide < 0 || table.Length != 65536 + 4 + wide * 9)
        {
            throw new DictionaryFormatException("The character table's range list has the wrong size.");
        }

        _wideRanges = new (int, int, byte)[wide];
        for (int i = 0; i < wide; i++)
        {
            int p = 65536 + 4 + i * 9;
            byte cls = table[p + 8];
            if (cls >= Classes.Count)
            {
                throw new DictionaryFormatException("The range list names a class that does not exist.");
            }

            _wideRanges[i] = (BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(p)), BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(p + 4)), cls);
        }

        _unknown = ReadUnknown(s[SecUnknown], Classes.Count, rightSize);
        for (int c = 0; c < _unknown.Length; c++)
        {
            foreach (UnknownEntry u in _unknown[c])
            {
                if (u.Pos >= _posTable.Length || u.Left >= _leftSize)
                {
                    throw new DictionaryFormatException("An unknown-word entry is out of range.");
                }
            }
        }

        int max = 1;
        for (int i = 0; i < n; i++)
        {
            max = Math.Max(max, offsets[i + 1] - offsets[i]);
        }

        _maxWordLength = max;
        Count = n;
        DefaultClass = FindClass("DEFAULT");
    }

    /// <summary>The number of words.</summary>
    public int Count { get; }

    /// <summary>The character classes; a class's index is what the unknown-word tables use.</summary>
    public IReadOnlyList<CharacterClass> Classes { get; }

    internal int RightSize { get; }

    internal int DefaultClass { get; }

    internal int MaxWordLength => _maxWordLength;

    /// <summary>Reads a dictionary file.</summary>
    /// <param name="file">The whole file.</param>
    /// <param name="limits">Ceilings checked before allocating; <see cref="DictionaryLimits.Default"/> when <see langword="null"/>.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="DictionaryFormatException">The file is malformed, truncated, fails its checksum or exceeds the limits.</exception>
    public static LatticeLexicon Load(ReadOnlySpan<byte> file, DictionaryLimits? limits = null)
    {
        limits ??= DictionaryLimits.Default;
        Dictionary<uint, byte[]> sections = DictionaryContainer.Read(file, Kind, limits);
        foreach (uint id in new[] { SecWords, SecOffsets, SecLeft, SecRight, SecCost, SecPos, SecExtra, SecPosTable, SecPool, SecMatrix, SecClasses, SecCharTable, SecUnknown })
        {
            if (!sections.ContainsKey(id))
            {
                throw new DictionaryFormatException($"Section {id} is missing.");
            }
        }

        return new LatticeLexicon(sections, limits);
    }

    /// <summary>Tries to read a dictionary file without throwing.</summary>
    /// <param name="file">The whole file.</param>
    /// <param name="dictionary">The dictionary on success.</param>
    /// <returns><see langword="true"/> when the file is valid.</returns>
    public static bool TryLoad(ReadOnlySpan<byte> file, out LatticeLexicon? dictionary)
    {
        try
        {
            dictionary = Load(file);
            return true;
        }
        catch (DictionaryFormatException)
        {
            dictionary = null;
            return false;
        }
    }

    /// <summary>The part-of-speech label of a word or an unknown entry, as the source dictionary spells it.</summary>
    /// <param name="word">The word index.</param>
    /// <returns>The label.</returns>
    public string PartOfSpeech(int word) => _posTable[_pos[word]];

    internal string PosName(int pos) => _posTable[pos];

    internal int PosCount => _posTable.Length;

    /// <summary>The extra text of a word: the dictionary form of an inflected Japanese word, or the morpheme expression of a Korean compound or inflected form; empty when none.</summary>
    /// <param name="word">The word index.</param>
    /// <returns>The text, or an empty string.</returns>
    public string Extra(int word) => _extra[word] < 0 ? string.Empty : Pool(_extra[word]);

    internal int ExtraIndex(int word) => _extra[word];

    internal string Pool(int index)
    {
        string? s = _poolCache[index];
        if (s is null)
        {
            s = new string(_poolChars, _poolOffsets[index], _poolOffsets[index + 1] - _poolOffsets[index]);
            _poolCache[index] = s;
        }

        return s;
    }

    internal ReadOnlySpan<char> Surface(int word) => _chars.AsSpan(_offsets[word], _offsets[word + 1] - _offsets[word]);

    internal int Left(int word) => _left[word];

    internal int Right(int word) => _right[word];

    internal int Cost(int word) => _cost[word];

    internal int PosIndex(int word) => _pos[word];

    internal int Connection(int previousRight, int nextLeft) => _matrix[previousRight * _leftSize + nextLeft];

    internal ReadOnlySpan<UnknownEntry> UnknownFor(int characterClass) => _unknown[characterClass];

    internal int ClassOf(int codePoint)
    {
        if (codePoint < 65536)
        {
            return _charTable[codePoint];
        }

        foreach ((int start, int end, byte cls) in _wideRanges)
        {
            if (codePoint >= start && codePoint <= end)
            {
                return cls;
            }
        }

        return DefaultClass;
    }

    private int FindClass(string name)
    {
        for (int i = 0; i < Classes.Count; i++)
        {
            if (Classes[i].Name == name)
            {
                return i;
            }
        }

        throw new DictionaryFormatException($"The dictionary has no '{name}' character class.");
    }

    /// <summary>
    /// Finds every word that is a prefix of <paramref name="text"/> by narrowing the sorted entries one character at a time. For each
    /// word found, its index and its length in UTF-16 units are appended to <paramref name="results"/>, shortest first.
    /// </summary>
    internal void CommonPrefixSearch(ReadOnlySpan<char> text, List<(int Word, int Length)> results)
    {
        int lo = 0, hi = Count;
        for (int k = 0; lo < hi; k++)
        {
            // Entries of exactly k characters sort first in the range, because they are prefixes of the others.
            if (k > 0)
            {
                while (lo < hi && _offsets[lo + 1] - _offsets[lo] == k)
                {
                    results.Add((lo, k));
                    lo++;
                }
            }

            if (k >= text.Length || lo >= hi)
            {
                break;
            }

            char c = text[k];
            int a = lo, b = hi;
            while (a < b)
            {
                int mid = (a + b) >>> 1;
                if (_chars[_offsets[mid] + k] < c)
                {
                    a = mid + 1;
                }
                else
                {
                    b = mid;
                }
            }

            int start = a;
            b = hi;
            while (a < b)
            {
                int mid = (a + b) >>> 1;
                if (_chars[_offsets[mid] + k] <= c)
                {
                    a = mid + 1;
                }
                else
                {
                    b = mid;
                }
            }

            lo = start;
            hi = a;
        }
    }

    private static ushort[] ReadUShorts(byte[] data, int n)
    {
        if (data.Length != n * 2)
        {
            throw new DictionaryFormatException("A column has the wrong length.");
        }

        var r = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            r[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2));
        }

        return r;
    }

    private static int[] ReadInts(byte[] data)
    {
        if (data.Length % 4 != 0)
        {
            throw new DictionaryFormatException("An integer column has a ragged length.");
        }

        var r = new int[data.Length / 4];
        for (int i = 0; i < r.Length; i++)
        {
            r[i] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i * 4));
        }

        return r;
    }

    private static string[] ReadStrings(byte[] data)
    {
        // Count-prefixed list of length-prefixed UTF-8 strings.
        if (data.Length < 4)
        {
            throw new DictionaryFormatException("A string table is truncated.");
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (count < 0 || count > data.Length)
        {
            throw new DictionaryFormatException("A string table's count is invalid.");
        }

        var r = new string[count];
        int p = 4;
        for (int i = 0; i < count; i++)
        {
            if (p + 4 > data.Length)
            {
                throw new DictionaryFormatException("A string table is truncated.");
            }

            int len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p));
            p += 4;
            if (len < 0 || len > data.Length - p)
            {
                throw new DictionaryFormatException("A string table entry runs past the table.");
            }

            r[i] = Encoding.UTF8.GetString(data, p, len);
            p += len;
        }

        return r;
    }

    private static (char[] Chars, int[] Offsets) ReadPool(byte[] data)
    {
        string[] all = ReadStrings(data);
        var offsets = new int[all.Length + 1];
        int total = 0;
        for (int i = 0; i < all.Length; i++)
        {
            offsets[i] = total;
            total += all[i].Length;
        }

        offsets[all.Length] = total;
        char[] chars = new char[total];
        for (int i = 0; i < all.Length; i++)
        {
            all[i].CopyTo(0, chars, offsets[i], all[i].Length);
        }

        return (chars, offsets);
    }

    private static CharacterClass[] ReadClasses(byte[] data)
    {
        // count, then per class: flags byte (1 invoke, 2 group), length byte, name (length-prefixed UTF-8).
        if (data.Length < 4)
        {
            throw new DictionaryFormatException("The class table is truncated.");
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (count < 1 || count > 255)
        {
            throw new DictionaryFormatException("The class count is invalid.");
        }

        var r = new CharacterClass[count];
        int p = 4;
        for (int i = 0; i < count; i++)
        {
            if (p + 4 > data.Length)
            {
                throw new DictionaryFormatException("The class table is truncated.");
            }

            byte flags = data[p], length = data[p + 1];
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 2));
            p += 4;
            if (nameLen > data.Length - p)
            {
                throw new DictionaryFormatException("A class name runs past the table.");
            }

            r[i] = new CharacterClass(Encoding.UTF8.GetString(data, p, nameLen), (flags & 1) != 0, (flags & 2) != 0, length);
            p += nameLen;
        }

        return r;
    }

    private static UnknownEntry[][] ReadUnknown(byte[] data, int classCount, int rightSize)
    {
        // per class: count u32, then count x (left u16, right u16, cost i16, pos u16).
        var r = new UnknownEntry[classCount][];
        int p = 0;
        for (int c = 0; c < classCount; c++)
        {
            if (p + 4 > data.Length)
            {
                throw new DictionaryFormatException("The unknown-word table is truncated.");
            }

            int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p));
            p += 4;
            if (count < 0 || count > (data.Length - p) / 8)
            {
                throw new DictionaryFormatException("The unknown-word table's count is invalid.");
            }

            r[c] = new UnknownEntry[count];
            for (int i = 0; i < count; i++, p += 8)
            {
                ushort right = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 2));
                if (right >= rightSize)
                {
                    throw new DictionaryFormatException("An unknown-word entry has a context id outside the matrix.");
                }

                r[c][i] = new UnknownEntry(
                    BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p)),
                    right,
                    BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p + 4)),
                    BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 6)));
            }
        }

        return r;
    }
}
