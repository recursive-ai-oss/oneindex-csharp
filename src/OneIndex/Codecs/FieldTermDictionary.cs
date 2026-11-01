using System.Text;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>
/// One field's term dictionary as stored in a terms file: the automaton of <see cref="FstBuilder"/> plus the term statistics
/// table. Lookups and enumeration read the resident bytes, validate every offset before using it and raise
/// <see cref="CorruptIndexException"/> naming the file; none allocates beyond the enumerator itself.
/// </summary>
internal sealed class FieldTermDictionary
{
    private const int MaxArcsPerNode = 256;
    private readonly byte[] _data;
    private readonly int _fstStart;
    private readonly int _fstLength;
    private readonly int _root;
    private readonly int _tableStart;
    private readonly int _tableLength;
    private readonly int _samplesStart;
    private readonly long _sampleCount;

    public FieldTermDictionary(string fileName, byte[] data, int fstStart, int fstLength, int root, int tableStart, int tableLength, int samplesStart, long sampleCount, long termCount, long totalTermBytes, int maxTermBytes)
    {
        FileName = fileName;
        _data = data;
        _fstStart = fstStart;
        _fstLength = fstLength;
        _root = root;
        _tableStart = tableStart;
        _tableLength = tableLength;
        _samplesStart = samplesStart;
        _sampleCount = sampleCount;
        TermCount = termCount;
        TotalTermBytes = totalTermBytes;
        MaxTermBytes = maxTermBytes;
    }

    public string FileName { get; }

    public long TermCount { get; }

    public long TotalTermBytes { get; }

    public int MaxTermBytes { get; }

    private ReadOnlySpan<byte> Fst => _data.AsSpan(_fstStart, _fstLength);

    public bool TryGetTerm(ReadOnlySpan<byte> term, out TermInfo info)
    {
        info = default;
        if (term.Length > MaxTermBytes || !TryFindOrdinal(term, out long ordinal))
        {
            return false;
        }

        var cursor = new InfoCursor { Ordinal = -1 };
        info = ReadInfo(ordinal, ref cursor);
        return true;
    }

    public ITermEnumerator GetTerms(ReadOnlySpan<byte> prefix) => Enumerate(prefix, null);

    /// <summary>Walks the terms of the dictionary that start with <paramref name="prefix"/> and match <paramref name="automaton"/> (or all of them when it is null).</summary>
    public ITermEnumerator Enumerate(ReadOnlySpan<byte> prefix, TermAutomaton? automaton)
    {
        if (TermCount == 0 || prefix.Length > MaxTermBytes)
        {
            return EmptyTermEnumerator.Instance;
        }

        int node = _root;
        long ordinal = 0;
        ReadOnlySpan<byte> fst = Fst;
        foreach (byte b in prefix)
        {
            int pos = ReadHeader(fst, node, out int arcCount, out bool isFinal);
            if (isFinal)
            {
                ordinal++;
            }

            if (!FindArc(fst, node, pos, arcCount, b, ref ordinal, out node))
            {
                return EmptyTermEnumerator.Instance;
            }
        }

        return new TermEnumerator(this, prefix, node, ordinal, automaton);
    }

    private bool TryFindOrdinal(ReadOnlySpan<byte> term, out long ordinal)
    {
        ordinal = 0;
        ReadOnlySpan<byte> fst = Fst;
        int node = _root;
        long ord = 0;
        foreach (byte b in term)
        {
            int pos = ReadHeader(fst, node, out int arcCount, out bool isFinal);
            if (isFinal)
            {
                ord++;
            }

            if (!FindArc(fst, node, pos, arcCount, b, ref ord, out node))
            {
                return false;
            }
        }

        ReadHeader(fst, node, out _, out bool final);
        if (!final)
        {
            return false;
        }

        if (ord >= TermCount)
        {
            throw Corrupt("term ordinal is past the term count");
        }

        ordinal = ord;
        return true;
    }

    // Finds the arc labelled 'label' in a node, adding the counts of the arcs before it to 'ordinal'.
    private bool FindArc(ReadOnlySpan<byte> fst, int node, int pos, int arcCount, byte label, ref long ordinal, out int target)
    {
        int previous = -1;
        for (int a = 0; a < arcCount; a++)
        {
            ReadArc(fst, node, ref pos, out byte l, out long words, out int t);
            if (l <= previous)
            {
                throw Corrupt("arc labels are not increasing");
            }

            previous = l;
            if (l == label)
            {
                target = t;
                return true;
            }

            if (l > label)
            {
                break;
            }

            ordinal += words;
        }

        target = 0;
        return false;
    }

    internal int ReadHeader(ReadOnlySpan<byte> fst, int node, out int arcCount, out bool isFinal)
    {
        int pos = node;
        ulong h = ReadVarUInt(fst, ref pos);
        arcCount = (int)(h >> 1);
        isFinal = (h & 1) != 0;
        if ((h >> 1) > MaxArcsPerNode)
        {
            throw Corrupt("a node claims more than 256 arcs");
        }

        return pos;
    }

    internal void ReadArc(ReadOnlySpan<byte> fst, int node, ref int pos, out byte label, out long words, out int target)
    {
        if ((uint)pos >= (uint)fst.Length)
        {
            throw Corrupt("an arc runs past the end of the automaton");
        }

        label = fst[pos++];
        ulong w = ReadVarUInt(fst, ref pos);
        ulong delta = ReadVarUInt(fst, ref pos);
        if (w > (ulong)TermCount || delta == 0 || delta > (ulong)node)
        {
            throw Corrupt("an arc has an invalid count or target");
        }

        words = (long)w;
        target = node - (int)delta;
    }

    private ulong ReadVarUInt(ReadOnlySpan<byte> span, ref int pos)
    {
        ulong result = 0;
        for (int shift = 0; shift < 63; shift += 7)
        {
            if ((uint)pos >= (uint)span.Length)
            {
                throw Corrupt("a number runs past the end of the data");
            }

            byte b = span[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw Corrupt("a number is too long");
    }

    internal CorruptIndexException Corrupt(string message) => new(FileName, "term dictionary: " + message);

    internal struct InfoCursor
    {
        public long Ordinal;
        public int Position;
        public long Offset;
        public int DocFreq;
        public long TotalTermFreq;
    }

    internal TermInfo ReadInfo(long ordinal, ref InfoCursor c)
    {
        if (ordinal < 0 || ordinal >= TermCount)
        {
            throw Corrupt("term ordinal is outside the term count");
        }

        ReadOnlySpan<byte> table = _data.AsSpan(_tableStart, _tableLength);
        if (c.Ordinal < 0 || ordinal < c.Ordinal || (ordinal / TermInfoBuilder.GroupSize) != (c.Ordinal / TermInfoBuilder.GroupSize))
        {
            long group = ordinal / TermInfoBuilder.GroupSize;
            if (group >= _sampleCount)
            {
                throw Corrupt("term statistics sample is missing");
            }

            ReadOnlySpan<byte> sample = _data.AsSpan(_samplesStart + (int)(group * 12), 12);
            long tablePos = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sample);
            if (tablePos >= _tableLength)
            {
                throw Corrupt("term statistics sample points outside the table");
            }

            c.Position = (int)tablePos;
            c.Offset = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(sample[4..]);
            c.Ordinal = group * TermInfoBuilder.GroupSize;
            ReadEntryHead(table, ref c);
        }

        while (c.Ordinal < ordinal)
        {
            c.Ordinal++;
            ReadEntryHead(table, ref c);
            ulong delta = ReadVarUInt(table, ref c.Position);
            if (delta > long.MaxValue || c.Offset + (long)delta < c.Offset)
            {
                throw Corrupt("postings offset overflows");
            }

            c.Offset += (long)delta;
        }

        return new TermInfo(c.DocFreq, c.TotalTermFreq, c.Offset);
    }

    private void ReadEntryHead(ReadOnlySpan<byte> table, ref InfoCursor c)
    {
        ulong df = ReadVarUInt(table, ref c.Position);
        ulong extra = ReadVarUInt(table, ref c.Position);
        if (df == 0 || df > int.MaxValue || extra > long.MaxValue / 2)
        {
            throw Corrupt("a term has an invalid document frequency");
        }

        c.DocFreq = (int)df;
        c.TotalTermFreq = (long)df + (long)extra;
    }

    /// <summary>Walks the automaton depth first in byte order, optionally intersected with a code-point automaton.</summary>
    private sealed class TermEnumerator : ITermEnumerator
    {
        private struct Frame
        {
            public int Node;
            public int ArcPos;
            public int ArcsLeft;
            public bool IsFinal;
            public bool SelfDone;
            public int StateOffset;
            public int StateLength;
            public int SavedTop;
            public int Partial;
            public int Need;
            public int LastLabel;
        }

        private readonly FieldTermDictionary _dict;
        private readonly TermAutomaton? _automaton;
        private readonly int _baseLength;
        private Frame[] _frames = new Frame[8];
        private int[] _pool = [];
        private int _poolTop;
        private byte[] _term;
        private int _depth;
        private long _nextOrdinal;
        private long _ordinal;
        private long _budget;
        private bool _done;
        private InfoCursor _cursor = new() { Ordinal = -1 };

        public TermEnumerator(FieldTermDictionary dict, ReadOnlySpan<byte> prefix, int node, long ordinal, TermAutomaton? automaton)
        {
            _dict = dict;
            _automaton = automaton;
            _baseLength = prefix.Length;
            _term = new byte[Math.Max(32, prefix.Length + 16)];
            prefix.CopyTo(_term);
            _nextOrdinal = ordinal;
            _budget = dict.TotalTermBytes + 2;
            int stateLength = 0;
            if (automaton is not null)
            {
                _pool = new int[Math.Max(8, automaton.MaxStateLength * 4)];
                stateLength = automaton.Start(_pool);
                bool dead = stateLength == 0;
                int pos = 0;
                while (!dead && pos < prefix.Length)
                {
                    if (Rune.DecodeFromUtf8(prefix[pos..], out Rune r, out int used) != System.Buffers.OperationStatus.Done)
                    {
                        dead = true;
                        break;
                    }

                    pos += used;
                    EnsurePool(stateLength + automaton.MaxStateLength);
                    int n = automaton.Transition(_pool.AsSpan(0, stateLength), r.Value, _pool.AsSpan(stateLength, automaton.MaxStateLength));
                    if (n == 0)
                    {
                        dead = true;
                        break;
                    }

                    // The new state replaces the old one at the bottom of the pool.
                    _pool.AsSpan(stateLength, n).CopyTo(_pool);
                    stateLength = n;
                }

                _poolTop = stateLength;

                if (dead)
                {
                    _done = true;
                    return;
                }
            }

            _frames[0] = default;
            InitFrame(ref _frames[0], node, 0, stateLength, 0, 0);
            _depth = 0;
        }

        public ReadOnlySpan<byte> Term => _term.AsSpan(0, _baseLength + _depth);

        public TermInfo Info => _dict.ReadInfo(_ordinal, ref _cursor);

        public bool MoveNext()
        {
            if (_done)
            {
                return false;
            }

            ReadOnlySpan<byte> fst = _dict.Fst;
            while (true)
            {
                if (_depth < 0)
                {
                    _done = true;
                    return false;
                }

                ref Frame f = ref _frames[_depth];
                if (!f.SelfDone)
                {
                    f.SelfDone = true;
                    if (f.IsFinal)
                    {
                        if (f.Need == 0 && (_automaton is null || _automaton.IsAccepting(_pool.AsSpan(f.StateOffset, f.StateLength))))
                        {
                            _ordinal = _nextOrdinal++;
                            if (_ordinal >= _dict.TermCount)
                            {
                                throw _dict.Corrupt("enumeration passed the term count");
                            }

                            return true;
                        }

                        _nextOrdinal++;
                    }
                }

                if (f.ArcsLeft == 0)
                {
                    _poolTop = f.SavedTop;
                    _depth--;
                    continue;
                }

                _dict.ReadArc(fst, f.Node, ref f.ArcPos, out byte label, out long words, out int target);
                f.ArcsLeft--;
                if (label <= f.LastLabel)
                {
                    throw _dict.Corrupt("arc labels are not increasing");
                }

                f.LastLabel = label;
                int childPartial = 0;
                int childNeed = 0;
                int stateOffset = f.StateOffset;
                int stateLength = f.StateLength;
                int saved = _poolTop;
                if (_automaton is not null)
                {
                    bool prune = false;
                    bool complete = false;
                    int cp = 0;
                    if (f.Need == 0)
                    {
                        if (label < 0x80)
                        {
                            cp = label;
                            complete = true;
                        }
                        else if (label is >= 0xC2 and <= 0xDF)
                        {
                            childNeed = 1;
                            childPartial = label & 0x1F;
                        }
                        else if (label is >= 0xE0 and <= 0xEF)
                        {
                            childNeed = 2;
                            childPartial = label & 0x0F;
                        }
                        else if (label is >= 0xF0 and <= 0xF4)
                        {
                            childNeed = 3;
                            childPartial = label & 0x07;
                        }
                        else
                        {
                            prune = true;
                        }
                    }
                    else if ((label & 0xC0) != 0x80)
                    {
                        prune = true;
                    }
                    else
                    {
                        childPartial = (f.Partial << 6) | (label & 0x3F);
                        childNeed = f.Need - 1;
                        if (childNeed == 0)
                        {
                            cp = childPartial;
                            complete = true;
                        }
                    }

                    if (!prune && complete && (cp is >= 0xD800 and <= 0xDFFF || cp > 0x10FFFF))
                    {
                        prune = true;
                    }

                    if (!prune && complete)
                    {
                        EnsurePool(_poolTop + _automaton.MaxStateLength);
                        int n = _automaton.Transition(_pool.AsSpan(f.StateOffset, f.StateLength), cp, _pool.AsSpan(_poolTop, _automaton.MaxStateLength));
                        if (n == 0)
                        {
                            prune = true;
                        }
                        else
                        {
                            stateOffset = _poolTop;
                            stateLength = n;
                            _poolTop += n;
                        }
                    }

                    if (prune)
                    {
                        _nextOrdinal += words;
                        continue;
                    }
                }

                if (--_budget < 0 || _baseLength + _depth + 1 > _dict.MaxTermBytes)
                {
                    throw _dict.Corrupt("automaton is deeper than its terms allow");
                }

                if (_term.Length <= _baseLength + _depth)
                {
                    Array.Resize(ref _term, _term.Length * 2);
                }

                _term[_baseLength + _depth] = label;
                if (_depth + 1 == _frames.Length)
                {
                    Array.Resize(ref _frames, _frames.Length * 2);
                }

                _depth++;
                InitFrame(ref _frames[_depth], target, stateOffset, stateLength, saved, childNeed, childPartial);
                fst = _dict.Fst;
            }
        }

        public void Dispose()
        {
        }

        private void InitFrame(ref Frame frame, int node, int stateOffset, int stateLength, int saved, int need, int partial = 0)
        {
            int pos = _dict.ReadHeader(_dict.Fst, node, out int arcCount, out bool isFinal);
            frame = new Frame
            {
                Node = node,
                ArcPos = pos,
                ArcsLeft = arcCount,
                IsFinal = isFinal,
                SelfDone = false,
                StateOffset = stateOffset,
                StateLength = stateLength,
                SavedTop = saved,
                Need = need,
                Partial = partial,
                LastLabel = -1,
            };
        }

        private void EnsurePool(int size)
        {
            if (_pool.Length < size)
            {
                Array.Resize(ref _pool, Math.Max(size, _pool.Length * 2));
            }
        }
    }

    private sealed class EmptyTermEnumerator : ITermEnumerator
    {
        public static readonly EmptyTermEnumerator Instance = new();

        public ReadOnlySpan<byte> Term => default;

        public TermInfo Info => default;

        public bool MoveNext() => false;

        public void Dispose()
        {
        }
    }
}
