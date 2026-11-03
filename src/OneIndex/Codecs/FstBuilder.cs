using System.Diagnostics.CodeAnalysis;

namespace OneIndex.Codecs;

/// <summary>
/// Builds the minimal acyclic automaton of a sorted term list incrementally: Daciuk, Mihov, Watson and Watson (2000), §4
/// "sorted data". Only the path of the last term is kept open; when the next term diverges, the nodes below the divergence are
/// frozen, each looked up in a register so that equal sub-automata are stored once. Frozen nodes are appended to a byte array,
/// children before parents, so every arc points to a lower address and the structure cannot contain a cycle.
/// </summary>
/// <remarks>
/// Node encoding: a varint <c>(arcCount &lt;&lt; 1) | isFinal</c>, then per arc its label byte, a varint count of the terms
/// reachable through the arc, and a varint distance back to the child's address. The per-arc counts number the terms in sorted
/// order (Lucchesi and Kowaltowski 1993, "perfect hashing" with automata): the ordinal of a term is the number of terms that
/// sort before it, found by summing the counts of the arcs passed over and one for every final node on the way.
/// </remarks>
internal sealed class FstBuilder
{
    private readonly ByteBuilder _out = new(1024);
    private readonly ByteBuilder _key = new(64);
    private readonly Dictionary<byte[], int> _register = new(ByteArrayComparer.Instance);
    private readonly Dictionary<byte[], int>.AlternateLookup<ReadOnlySpan<byte>> _lookup;
    private readonly int _maxTermBytes;
    private readonly int _version;
    private BuildNode[] _stack = new BuildNode[16];
    private byte[] _previous = new byte[16];
    private int _previousLength;
    private bool _any;

    /// <summary>Starts an empty automaton.</summary>
    /// <param name="maxTermBytes">The longest term accepted.</param>
    /// <param name="version">The node layout to write: 1 (arcs read in sequence) or 2 (fixed-width tables, arcs found by label).</param>
    public FstBuilder(int maxTermBytes, int version = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(version, 2);
        _version = version;
        _maxTermBytes = maxTermBytes;
        _lookup = _register.GetAlternateLookup<ReadOnlySpan<byte>>();
        _stack[0] = new BuildNode();
    }

    public long TermCount { get; private set; }

    public long TotalTermBytes { get; private set; }

    public int Add(ReadOnlySpan<byte> term)
    {
        if (term.Length > _maxTermBytes)
        {
            throw new ArgumentException($"A term of {term.Length} bytes exceeds the limit of {_maxTermBytes}.", nameof(term));
        }

        int prefix = 0;
        if (_any)
        {
            int cmp = term.SequenceCompareTo(_previous.AsSpan(0, _previousLength));
            if (cmp <= 0)
            {
                throw new ArgumentException("Terms must be added in strictly increasing byte order.", nameof(term));
            }

            int max = Math.Min(term.Length, _previousLength);
            while (prefix < max && term[prefix] == _previous[prefix])
            {
                prefix++;
            }

            FreezeDownTo(prefix);
        }

        if (_stack.Length <= term.Length + 1)
        {
            Array.Resize(ref _stack, Math.Max(_stack.Length * 2, term.Length + 2));
        }

        for (int i = prefix; i < term.Length; i++)
        {
            _stack[i] ??= new BuildNode();
            _stack[i].AddArc(term[i]);
            (_stack[i + 1] ??= new BuildNode()).Reset();
        }

        _stack[term.Length].IsFinal = true;
        if (_previous.Length < term.Length)
        {
            _previous = new byte[Math.Max(term.Length, _previous.Length * 2)];
        }

        term.CopyTo(_previous);
        _previousLength = term.Length;
        _any = true;
        TermCount++;
        TotalTermBytes += term.Length;
        return prefix;
    }

    /// <summary>Freezes everything and returns the automaton bytes and the address of the root.</summary>
    public (byte[] Bytes, int Root) Finish()
    {
        FreezeDownTo(0);
        int root = Freeze(_stack[0]);
        return (_out.Span.ToArray(), root);
    }

    // Freezes the open nodes deeper than 'depth' on the last term's path, deepest first, linking each into its parent's last arc.
    private void FreezeDownTo(int depth)
    {
        for (int d = _previousLength; d > depth; d--)
        {
            BuildNode node = _stack[d];
            long words = node.Words;
            int address = Freeze(node);
            _stack[d - 1].SetLastArcTarget(address, words);
        }
    }

    private int Freeze(BuildNode node)
    {
        // The register key spells the node with absolute child addresses, so two nodes are equal exactly when their
        // sub-automata are; the stored form uses distances, which depend on where the node lands.
        _key.Clear();
        _key.WriteVarUInt(((ulong)node.ArcCount << 1) | (node.IsFinal ? 1UL : 0));
        for (int i = 0; i < node.ArcCount; i++)
        {
            _key.WriteByte(node.Labels[i]);
            _key.WriteVarUInt((ulong)node.Words_[i]);
            _key.WriteVarUInt((ulong)node.Targets[i]);
        }

        if (_lookup.TryGetValue(_key.Span, out int existing))
        {
            return existing;
        }

        int address = _out.Length;
        if (_version >= 2)
        {
            WriteNode2(node, address);
        }
        else
        {
            _out.WriteVarUInt(((ulong)node.ArcCount << 1) | (node.IsFinal ? 1UL : 0));
            for (int i = 0; i < node.ArcCount; i++)
            {
                _out.WriteByte(node.Labels[i]);
                _out.WriteVarUInt((ulong)node.Words_[i]);
                _out.WriteVarUInt((ulong)(address - node.Targets[i]));
            }
        }

        _register.Add(_key.Span.ToArray(), address);
        return address;
    }

    // Version 2 node: one head byte (bit 0 final, bits 1-2 and 3-4 the byte widths of the cumulative counts and of the child
    // distances, minus one, bits 5-7 the arc count when it is below 7, else 7 followed by a varint of count minus 7), then the labels,
    // then the cumulative word counts (arc i holds the words of arcs 0 to i), then the distances to the children, little endian. A
    // reader finds an arc by label in the label table and its ordinal offset in one table read.
    private void WriteNode2(BuildNode node, int address)
    {
        int n = node.ArcCount;
        long total = 0;
        long maxDelta = 0;
        for (int i = 0; i < n; i++)
        {
            total += node.Words_[i];
            maxDelta = Math.Max(maxDelta, address - node.Targets[i]);
        }

        int cw = n == 0 ? 1 : Width(total);
        int dw = n == 0 ? 1 : Width(maxDelta);
        int code = Math.Min(n, 7);
        _out.WriteByte((byte)((node.IsFinal ? 1 : 0) | ((cw - 1) << 1) | ((dw - 1) << 3) | (code << 5)));
        if (code == 7)
        {
            _out.WriteVarUInt((ulong)(n - 7));
        }

        for (int i = 0; i < n; i++)
        {
            _out.WriteByte(node.Labels[i]);
        }

        long running = 0;
        for (int i = 0; i < n; i++)
        {
            running += node.Words_[i];
            WriteFixed(running, cw);
        }

        for (int i = 0; i < n; i++)
        {
            WriteFixed(address - node.Targets[i], dw);
        }
    }

    private static int Width(long value) => value < 1L << 8 ? 1 : value < 1L << 16 ? 2 : value < 1L << 24 ? 3 : 4;

    private void WriteFixed(long value, int width)
    {
        for (int k = 0; k < width; k++)
        {
            _out.WriteByte((byte)(value >> (8 * k)));
        }
    }

    private sealed class BuildNode
    {
        public byte[] Labels = new byte[4];
        public long[] Words_ = new long[4];
        public int[] Targets = new int[4];
        public int ArcCount;
        public bool IsFinal;

        public long Words
        {
            get
            {
                long w = IsFinal ? 1 : 0;
                for (int i = 0; i < ArcCount; i++)
                {
                    w += Words_[i];
                }

                return w;
            }
        }

        public void Reset()
        {
            ArcCount = 0;
            IsFinal = false;
        }

        public void AddArc(byte label)
        {
            if (ArcCount == Labels.Length)
            {
                Array.Resize(ref Labels, ArcCount * 2);
                Array.Resize(ref Words_, ArcCount * 2);
                Array.Resize(ref Targets, ArcCount * 2);
            }

            Labels[ArcCount] = label;
            Words_[ArcCount] = 0;
            Targets[ArcCount] = -1;
            ArcCount++;
        }

        public void SetLastArcTarget(int address, long words)
        {
            Targets[ArcCount - 1] = address;
            Words_[ArcCount - 1] = words;
        }
    }

    internal sealed class ByteArrayComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => Hash(obj);

        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

        public int GetHashCode(ReadOnlySpan<byte> alternate) => Hash(alternate);

        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();

        // FNV-1a. The register only decides sharing, never the order of anything written, but a fixed function keeps runs repeatable.
        private static int Hash(ReadOnlySpan<byte> bytes)
        {
            uint h = 2166136261;
            foreach (byte b in bytes)
            {
                h = (h ^ b) * 16777619;
            }

            return (int)h;
        }
    }
}

/// <summary>Builds a field's table of term statistics, indexed by term ordinal: document frequency, extra occurrences and the postings offset, as varints, with a sample every 32 terms for random access.</summary>
internal sealed class TermInfoBuilder
{
    public const int GroupSize = 32;
    private readonly ByteBuilder _table = new(1024);
    private readonly ByteBuilder _samples = new(256);
    private long _previousOffset;
    private long _count;

    public long Count => _count;

    public ReadOnlySpan<byte> Table => _table.Span;

    public ReadOnlySpan<byte> Samples => _samples.Span;

    public void Add(int docFreq, long totalTermFreq, long postingsOffset)
    {
        if ((_count % GroupSize) == 0)
        {
            _samples.WriteUInt32((uint)_table.Length);
            _samples.WriteUInt64((ulong)postingsOffset);
            _table.WriteVarUInt((ulong)docFreq);
            _table.WriteVarUInt((ulong)(totalTermFreq - docFreq));
        }
        else
        {
            if (postingsOffset < _previousOffset)
            {
                throw new InvalidOperationException("Postings offsets must not decrease within a field.");
            }

            _table.WriteVarUInt((ulong)docFreq);
            _table.WriteVarUInt((ulong)(totalTermFreq - docFreq));
            _table.WriteVarUInt((ulong)(postingsOffset - _previousOffset));
        }

        _previousOffset = postingsOffset;
        _count++;
    }
}
