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
    private readonly int _version;
    private readonly byte[] _data;
    private readonly int _fstStart;
    private readonly int _fstLength;
    private readonly int _root;
    private readonly int _tableStart;
    private readonly int _tableLength;
    private readonly int _samplesStart;
    private readonly long _sampleCount;

    public FieldTermDictionary(string fileName, int version, byte[] data, int fstStart, int fstLength, int root, int tableStart, int tableLength, int samplesStart, long sampleCount, long termCount, long totalTermBytes, int maxTermBytes)
    {
        FileName = fileName;
        _version = version;
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
            ReadNode(fst, node, out NodeView view);
            if (view.IsFinal)
            {
                ordinal++;
            }

            if (!FindArc(fst, node, in view, b, ref ordinal, out node))
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
            ReadNode(fst, node, out NodeView view);
            if (view.IsFinal)
            {
                ord++;
            }

            if (!FindArc(fst, node, in view, b, ref ord, out node))
            {
                return false;
            }
        }

        ReadNode(fst, node, out NodeView last);
        if (!last.IsFinal)
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
    private bool FindArc(ReadOnlySpan<byte> fst, int node, in NodeView view, byte label, ref long ordinal, out int target)
    {
        if (_version >= 2)
        {
            // Labels are stored together, so the arc is found without decoding the others, and the words before it are one table read.
            int at = fst.Slice(view.Labels, view.ArcCount).IndexOf(label);
            if (at < 0)
            {
                target = 0;
                return false;
            }

            ReadArc2(fst, node, in view, at, out _, out long before, out _, out target);
            ordinal += before;
            return true;
        }

        int pos = view.ArcPos;
        int previous = -1;
        for (int a = 0; a < view.ArcCount; a++)
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

    /// <summary>Where a node's parts are. Version 1: <see cref="ArcPos"/> is the first arc, read in sequence. Version 2: labels, cumulative word counts and child distances are three tables at fixed widths.</summary>
    internal struct NodeView
    {
        public int ArcCount;
        public bool IsFinal;
        public int ArcPos;
        public int Labels;
        public int Cum;
        public int Delta;
        public int CumWidth;
        public int DeltaWidth;
    }

    internal void ReadNode(ReadOnlySpan<byte> fst, int node, out NodeView view)
    {
        view = default;
        if (_version < 2)
        {
            view.ArcPos = ReadHeader(fst, node, out view.ArcCount, out view.IsFinal);
            return;
        }

        if ((uint)node >= (uint)fst.Length)
        {
            throw Corrupt("a node lies outside the automaton");
        }

        byte head = fst[node];
        view.IsFinal = (head & 1) != 0;
        view.CumWidth = ((head >> 1) & 3) + 1;
        view.DeltaWidth = ((head >> 3) & 3) + 1;
        int pos = node + 1;
        int code = head >> 5;
        if (code == 7)
        {
            ulong extra = ReadVarUInt(fst, ref pos);
            if (extra > MaxArcsPerNode - 7)
            {
                throw Corrupt("a node claims more than 256 arcs");
            }

            view.ArcCount = 7 + (int)extra;
        }
        else
        {
            view.ArcCount = code;
        }

        view.Labels = pos;
        view.Cum = pos + view.ArcCount;
        view.Delta = view.Cum + (view.ArcCount * view.CumWidth);
        if ((long)view.Delta + ((long)view.ArcCount * view.DeltaWidth) > fst.Length)
        {
            throw Corrupt("a node runs past the end of the automaton");
        }
    }

    // Reads arc i of a version 2 node: its label, the words before it, the words through it and its child. The node was bounds
    // checked by ReadNode, so the reads below stay inside the automaton.
    internal void ReadArc2(ReadOnlySpan<byte> fst, int node, in NodeView view, int i, out byte label, out long before, out long inclusive, out int target)
    {
        label = fst[view.Labels + i];
        before = i == 0 ? 0 : ReadFixed(fst, view.Cum + ((i - 1) * view.CumWidth), view.CumWidth);
        inclusive = ReadFixed(fst, view.Cum + (i * view.CumWidth), view.CumWidth);
        long delta = ReadFixed(fst, view.Delta + (i * view.DeltaWidth), view.DeltaWidth);
        if (inclusive < before || inclusive > TermCount || delta == 0 || delta > node)
        {
            throw Corrupt("an arc has an invalid count or target");
        }

        target = node - (int)delta;
    }

    // Little-endian unsigned number of 1 to 4 bytes; the caller has checked that they lie inside the automaton.
    private static long ReadFixed(ReadOnlySpan<byte> fst, int pos, int width)
    {
        // One four-byte load and a mask when four bytes remain; the few numbers at the very end of the automaton take the byte loop.
        if (fst.Length >= 4 && (uint)pos <= (uint)(fst.Length - 4))
        {
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fst.Slice(pos, 4)) & (0xFFFFFFFFu >> (8 * (4 - width)));
        }

        return ReadFixedAtEnd(fst, pos, width);
    }

    private static long ReadFixedAtEnd(ReadOnlySpan<byte> fst, int pos, int width) => width switch
    {
        1 => fst[pos],
        2 => fst[pos] | ((long)fst[pos + 1] << 8),
        3 => fst[pos] | ((long)fst[pos + 1] << 8) | ((long)fst[pos + 2] << 16),
        _ => (uint)(fst[pos] | (fst[pos + 1] << 8) | (fst[pos + 2] << 16) | (fst[pos + 3] << 24)),
    };

    // The words of all arcs of a version 2 node (the last cumulative entry).
    internal static long ReadArcTotal(ReadOnlySpan<byte> fst, in NodeView view) => ReadFixed(fst, view.Cum + ((view.ArcCount - 1) * view.CumWidth), view.CumWidth);

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
            public NodeView View;
            public int ArcPos;
            public int ArcsLeft;
            public int Index;
            public long Consumed;
            public ulong NarrowBytes;
            public int NarrowCount;
            public int NarrowNext;
            public int SearchFrom;
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
        private readonly int[] _viableState = new int[4];
        private readonly byte[] _viableScratch = new byte[8]; // TryGetViableBytes writes at most 8
        private int _viableLength;
        private int _viableCount;
        private ulong _viableBytes;

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

                byte label;
                long words;
                int target;
                if (_dict._version >= 2)
                {
                    if (!NextArc2(ref f, fst, out label, out words, out target))
                    {
                        _poolTop = f.SavedTop;
                        _depth--;
                        continue;
                    }
                }
                else
                {
                    if (f.ArcsLeft == 0)
                    {
                        _poolTop = f.SavedTop;
                        _depth--;
                        continue;
                    }

                    _dict.ReadArc(fst, f.Node, ref f.ArcPos, out label, out words, out target);
                    f.ArcsLeft--;
                }

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

                // The child is set up in the next slot and entered only if something below it can still match.
                if (!InitFrame(ref _frames[_depth + 1], target, stateOffset, stateLength, saved, childNeed, childPartial, canSkip: _automaton is not null))
                {
                    _poolTop = saved;
                    _nextOrdinal += words;
                    continue;
                }

                _depth++;
                fst = _dict.Fst;
            }
        }

        public void Dispose()
        {
        }

        // The next arc of a version 2 node, in label order. When the automaton can say which first bytes keep it alive (a
        // Levenshtein state with no edits left reads only the word's own letters), only those arcs are looked up, directly; the
        // words of the arcs passed over are added to the running ordinal from the cumulative table. Returns false at the end.
        private bool NextArc2(ref Frame f, ReadOnlySpan<byte> fst, out byte label, out long words, out int target)
        {
            ref readonly NodeView v = ref f.View;
            int i;
            if (f.NarrowCount >= 0)
            {
                i = -1;
                ReadOnlySpan<byte> labels = fst.Slice(v.Labels, v.ArcCount);
                while (f.NarrowNext < f.NarrowCount)
                {
                    int at = labels[f.SearchFrom..].IndexOf((byte)(f.NarrowBytes >> (8 * f.NarrowNext++)));
                    if (at >= 0)
                    {
                        i = f.SearchFrom + at;
                        f.SearchFrom = i + 1;
                        break;
                    }
                }
            }
            else
            {
                i = f.Index < v.ArcCount ? f.Index++ : -1;
            }

            if (i < 0)
            {
                // The rest of the node's terms are passed over.
                if (v.ArcCount > 0)
                {
                    _nextOrdinal += ReadArcTotal(fst, in v) - f.Consumed;
                    f.Consumed = ReadArcTotal(fst, in v);
                }

                label = 0;
                words = 0;
                target = 0;
                return false;
            }

            // Arcs taken in sequence know the words before them (the previous arc's cumulative count): one table read less.
            long before = f.NarrowCount >= 0 ? (i == 0 ? 0 : ReadFixed(fst, v.Cum + ((i - 1) * v.CumWidth), v.CumWidth)) : f.Consumed;
            label = fst[v.Labels + i];
            long inclusive = ReadFixed(fst, v.Cum + (i * v.CumWidth), v.CumWidth);
            long delta = ReadFixed(fst, v.Delta + (i * v.DeltaWidth), v.DeltaWidth);
            if (inclusive < before || inclusive > _dict.TermCount || delta == 0 || delta > f.Node)
            {
                throw _dict.Corrupt("an arc has an invalid count or target");
            }

            target = f.Node - (int)delta;
            _nextOrdinal += before - f.Consumed;
            words = inclusive - before;
            f.Consumed = inclusive;
            return true;
        }

        // Sets up a frame for a node. With canSkip, returns false when nothing at or below the node can match: it is not a term the
        // automaton accepts, and either it has no arcs or the automaton can say that only a few first bytes keep it alive and the
        // node has none of them.
        private bool InitFrame(ref Frame frame, int node, int stateOffset, int stateLength, int saved, int need, int partial = 0, bool canSkip = false)
        {
            _dict.ReadNode(_dict.Fst, node, out frame.View);
            int arcCount = frame.View.ArcCount;
            ulong narrowBytes = 0;
            int narrowCount = -1;
            if (_dict._version >= 2 && _automaton is not null && need == 0 && arcCount > 0 && stateLength > 0)
            {
                bool same = stateLength == _viableLength;
                for (int k = 0; same && k < stateLength; k++)
                {
                    same = _pool[stateOffset + k] == _viableState[k];
                }

                if (same)
                {
                    // Sibling arcs that the automaton treats alike lead to the same state; its answer is kept.
                    narrowCount = _viableCount;
                    narrowBytes = _viableBytes;
                }
                else
                {
                    ReadOnlySpan<int> state = _pool.AsSpan(stateOffset, stateLength);
                    narrowCount = _automaton.TryGetViableBytes(state, _viableScratch);
                    for (int k = 0; k < narrowCount; k++)
                    {
                        narrowBytes |= (ulong)_viableScratch[k] << (8 * k);
                    }

                    if (stateLength <= _viableState.Length)
                    {
                        state.CopyTo(_viableState);
                        _viableLength = stateLength;
                        _viableCount = narrowCount;
                        _viableBytes = narrowBytes;
                    }
                }
            }

            if (canSkip && need == 0 && stateLength > 0 && !(frame.View.IsFinal && _automaton!.IsAccepting(_pool.AsSpan(stateOffset, stateLength))))
            {
                if (arcCount == 0)
                {
                    return false;
                }

                if (narrowCount >= 0)
                {
                    ReadOnlySpan<byte> labels = _dict.Fst.Slice(frame.View.Labels, arcCount);
                    bool any = false;
                    for (int k = 0; k < narrowCount && !any; k++)
                    {
                        any = labels.Contains((byte)(narrowBytes >> (8 * k)));
                    }

                    if (!any)
                    {
                        return false;
                    }
                }
            }

            frame.Node = node;
            frame.NarrowBytes = narrowBytes;
            frame.NarrowCount = narrowCount;
            frame.ArcPos = frame.View.ArcPos;
            frame.ArcsLeft = arcCount;
            frame.Index = 0;
            frame.Consumed = 0;
            frame.NarrowNext = 0;
            frame.SearchFrom = 0;
            frame.IsFinal = frame.View.IsFinal;
            frame.SelfDone = false;
            frame.StateOffset = stateOffset;
            frame.StateLength = stateLength;
            frame.SavedTop = saved;
            frame.Need = need;
            frame.Partial = partial;
            frame.LastLabel = -1;
            return true;
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
