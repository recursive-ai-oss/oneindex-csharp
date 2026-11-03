using System.Buffers.Binary;
using System.Text;

namespace OneIndex.Analysis.Asian;

/// <summary>Builds the bytes of a <see cref="LatticeLexicon"/> file from parsed source data. Used by the dictionary build tool and the tests.</summary>
internal sealed class LatticeLexiconBuilder
{
    private readonly List<(string Surface, ushort Left, ushort Right, short Cost, string Pos, string? Extra)> _entries = [];
    private readonly List<CharacterClass> _classes = [];
    private readonly List<(int Start, int End, int Class)> _ranges = [];
    private readonly List<(int Class, ushort Left, ushort Right, short Cost, string Pos)> _unknown = [];
    private short[] _matrix = [];
    private int _rightSize, _leftSize;

    internal void AddWord(string surface, int left, int right, int cost, string pos, string? extra)
    {
        if (surface.Length == 0)
        {
            return;
        }

        _entries.Add((surface, checked((ushort)left), checked((ushort)right), (short)Math.Clamp(cost, short.MinValue, short.MaxValue), pos, string.IsNullOrEmpty(extra) ? null : extra));
    }

    internal void SetMatrix(int rightSize, int leftSize, short[] data)
    {
        _rightSize = rightSize;
        _leftSize = leftSize;
        _matrix = data;
    }

    internal int AddClass(string name, bool invoke, bool group, int length)
    {
        _classes.Add(new CharacterClass(name, invoke, group, length));
        return _classes.Count - 1;
    }

    internal void MapRange(int start, int end, string className)
    {
        int idx = _classes.FindIndex(c => c.Name == className);
        if (idx < 0)
        {
            throw new ArgumentException($"Unknown class {className}.");
        }

        _ranges.Add((start, end, idx));
    }

    internal void AddUnknown(string className, int left, int right, int cost, string pos)
    {
        int idx = _classes.FindIndex(c => c.Name == className);
        if (idx < 0)
        {
            throw new ArgumentException($"Unknown class {className}.");
        }

        _unknown.Add((idx, (ushort)left, (ushort)right, (short)cost, pos));
    }

    internal byte[] Build()
    {
        _entries.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Surface, b.Surface);
            if (c != 0)
            {
                return c;
            }

            c = a.Cost.CompareTo(b.Cost);
            if (c != 0)
            {
                return c;
            }

            c = a.Left.CompareTo(b.Left);
            return c != 0 ? c : a.Right.CompareTo(b.Right);
        });

        var posIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var posList = new List<string>();
        int PosId(string p)
        {
            if (!posIds.TryGetValue(p, out int id))
            {
                id = posList.Count;
                posIds[p] = id;
                posList.Add(p);
            }

            return id;
        }

        var poolIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var poolList = new List<string>();
        int n = _entries.Count;
        var chars = new MemoryStream();
        var offsets = new byte[(n + 1) * 4];
        var left = new byte[n * 2];
        var right = new byte[n * 2];
        var cost = new byte[n * 2];
        var pos = new byte[n * 2];
        var extra = new byte[n * 4];
        int total = 0;
        byte[] two = new byte[2];
        for (int i = 0; i < n; i++)
        {
            var e = _entries[i];
            BinaryPrimitives.WriteInt32LittleEndian(offsets.AsSpan(i * 4), total);
            foreach (char ch in e.Surface)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(two, ch);
                chars.Write(two);
            }

            total += e.Surface.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(left.AsSpan(i * 2), e.Left);
            BinaryPrimitives.WriteUInt16LittleEndian(right.AsSpan(i * 2), e.Right);
            BinaryPrimitives.WriteInt16LittleEndian(cost.AsSpan(i * 2), e.Cost);
            BinaryPrimitives.WriteUInt16LittleEndian(pos.AsSpan(i * 2), (ushort)PosId(e.Pos));
            int x = -1;
            if (e.Extra is not null)
            {
                if (!poolIds.TryGetValue(e.Extra, out x))
                {
                    x = poolList.Count;
                    poolIds[e.Extra] = x;
                    poolList.Add(e.Extra);
                }
            }

            BinaryPrimitives.WriteInt32LittleEndian(extra.AsSpan(i * 4), x);
        }

        BinaryPrimitives.WriteInt32LittleEndian(offsets.AsSpan(n * 4), total);

        byte[] matrix = new byte[4 + _matrix.Length * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(matrix, (ushort)_rightSize);
        BinaryPrimitives.WriteUInt16LittleEndian(matrix.AsSpan(2), (ushort)_leftSize);
        for (int i = 0; i < _matrix.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(matrix.AsSpan(4 + i * 2), _matrix[i]);
        }

        // Unknown entries per class (this also registers their parts of speech).
        var unknown = new MemoryStream();
        for (int c = 0; c < _classes.Count; c++)
        {
            var list = _unknown.Where(u => u.Class == c).ToList();
            WriteInt(unknown, list.Count);
            foreach (var u in list)
            {
                WriteUShort(unknown, u.Left);
                WriteUShort(unknown, u.Right);
                WriteUShort(unknown, (ushort)u.Cost);
                WriteUShort(unknown, (ushort)PosId(u.Pos));
            }
        }

        var classes = new MemoryStream();
        WriteInt(classes, _classes.Count);
        foreach (CharacterClass cc in _classes)
        {
            classes.WriteByte((byte)((cc.Invoke ? 1 : 0) | (cc.Group ? 2 : 0)));
            classes.WriteByte((byte)cc.Length);
            byte[] name = Encoding.UTF8.GetBytes(cc.Name);
            WriteUShort(classes, (ushort)name.Length);
            classes.Write(name);
        }

        int defaultClass = _classes.FindIndex(c => c.Name == "DEFAULT");
        byte[] table = new byte[65536];
        Array.Fill(table, (byte)Math.Max(defaultClass, 0));
        var wide = new List<(int, int, int)>();
        foreach ((int start, int end, int cls) in _ranges)
        {
            for (int cp = start; cp <= Math.Min(end, 65535); cp++)
            {
                table[cp] = (byte)cls;
            }

            if (end >= 65536)
            {
                wide.Add((Math.Max(start, 65536), end, cls));
            }
        }

        var charTable = new MemoryStream();
        charTable.Write(table);
        WriteInt(charTable, wide.Count);
        foreach ((int s, int e, int cls) in wide)
        {
            WriteInt(charTable, s);
            WriteInt(charTable, e);
            charTable.WriteByte((byte)cls);
        }

        return DictionaryContainer.Write(LatticeLexicon.Kind,
        [
            (LatticeLexicon.SecWords, chars.ToArray()),
            (LatticeLexicon.SecOffsets, offsets),
            (LatticeLexicon.SecLeft, left),
            (LatticeLexicon.SecRight, right),
            (LatticeLexicon.SecCost, cost),
            (LatticeLexicon.SecPos, pos),
            (LatticeLexicon.SecExtra, extra),
            (LatticeLexicon.SecPosTable, StringTable(posList)),
            (LatticeLexicon.SecPool, StringTable(poolList)),
            (LatticeLexicon.SecMatrix, matrix),
            (LatticeLexicon.SecClasses, classes.ToArray()),
            (LatticeLexicon.SecCharTable, charTable.ToArray()),
            (LatticeLexicon.SecUnknown, unknown.ToArray()),
        ]);
    }

    internal static byte[] StringTable(IReadOnlyList<string> strings)
    {
        var ms = new MemoryStream();
        WriteInt(ms, strings.Count);
        foreach (string s in strings)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            WriteInt(ms, b.Length);
            ms.Write(b);
        }

        return ms.ToArray();
    }

    private static void WriteInt(Stream s, int v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteUShort(Stream s, ushort v)
    {
        byte[] b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        s.Write(b);
    }
}
