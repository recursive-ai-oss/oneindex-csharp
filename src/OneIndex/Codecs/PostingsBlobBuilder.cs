using System.Buffers.Binary;

namespace OneIndex.Codecs;

/// <summary>
/// Builds one term's postings blob (see docs/postings.md): documents are cut into blocks of 128; each block stores its document
/// gaps and frequencies bit-packed at the narrowest width that fits the block, then its positions as varints. A skip entry per
/// block records the last document, the data offset and the block's impact (largest frequency, shortest field), which block-max
/// WAND uses to bound the block's score without decoding it.
/// </summary>
internal sealed class PostingsBlobBuilder
{
    public const int BlockSize = 128;
    private readonly uint[] _gaps = new uint[BlockSize];
    private readonly uint[] _freqs = new uint[BlockSize];
    private readonly ByteBuilder _data = new(1024);
    private readonly ByteBuilder _positions = new(1024);
    private readonly ByteBuilder _blob = new(1024);
    private readonly List<(int LastDoc, uint DataOffset, int MaxFreq, int MinLen)> _entries = [];
    private int[] _fieldLengths = [];
    private int _maxDoc;
    private bool _hasPositions;
    private bool _hasOffsets;
    private int _inBlock;
    private int _lastDoc;
    private int _blockLastDoc;
    private int _blockMaxFreq;
    private int _blockMinLen;
    private int _termMaxFreq;
    private int _termMinLen;
    private int _positionsExpected;
    private int _prevPosition;
    private int _prevStart;
    private bool _inDoc;

    public int DocFreq { get; private set; }

    public long TotalTermFreq { get; private set; }

    public ReadOnlySpan<byte> Blob => _blob.Span;

    public void Begin(bool hasPositions, bool hasOffsets, int[] fieldLengths, int maxDoc)
    {
        _hasPositions = hasPositions;
        _hasOffsets = hasOffsets;
        _fieldLengths = fieldLengths;
        _maxDoc = maxDoc;
        _data.Clear();
        _positions.Clear();
        _blob.Clear();
        _entries.Clear();
        _inBlock = 0;
        _lastDoc = -1;
        _blockLastDoc = -1;
        _termMaxFreq = 0;
        _termMinLen = int.MaxValue;
        _blockMaxFreq = 0;
        _blockMinLen = int.MaxValue;
        DocFreq = 0;
        TotalTermFreq = 0;
        _positionsExpected = 0;
        _inDoc = false;
    }

    public void AddDocument(int docId, int freq)
    {
        CheckDocComplete();
        if (docId <= _lastDoc || docId >= _maxDoc)
        {
            throw new ArgumentOutOfRangeException(nameof(docId), $"Document {docId} is not above the previous document {_lastDoc} and below {_maxDoc}.");
        }

        if (freq < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(freq), "A document in a postings list has a frequency of at least 1.");
        }

        if (_inBlock == BlockSize)
        {
            FlushBlock();
        }

        int prev = _inBlock == 0 ? _blockLastDoc : _lastDoc;
        _gaps[_inBlock] = (uint)(docId - prev - 1);
        _freqs[_inBlock] = (uint)(freq - 1);
        _inBlock++;
        _lastDoc = docId;
        int len = _fieldLengths[docId];
        _blockMaxFreq = Math.Max(_blockMaxFreq, freq);
        _blockMinLen = Math.Min(_blockMinLen, len);
        DocFreq++;
        TotalTermFreq += freq;
        _positionsExpected = _hasPositions ? freq : 0;
        _prevPosition = 0;
        _prevStart = 0;
        _inDoc = true;
    }

    public void AddPosition(int position, int startOffset, int endOffset)
    {
        if (!_hasPositions)
        {
            throw new InvalidOperationException("The field was declared without positions.");
        }

        if (!_inDoc || _positionsExpected == 0)
        {
            throw new InvalidOperationException("More positions than the document's frequency.");
        }

        if (position < _prevPosition)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Positions within a document must not decrease.");
        }

        _positions.WriteVarUInt((uint)(position - _prevPosition));
        _prevPosition = position;
        if (_hasOffsets)
        {
            if (startOffset < 0 || endOffset < startOffset)
            {
                throw new ArgumentOutOfRangeException(nameof(endOffset), "Offsets must satisfy 0 <= start <= end.");
            }

            long delta = (long)startOffset - _prevStart;
            _positions.WriteVarUInt((ulong)((delta << 1) ^ (delta >> 63)));
            _positions.WriteVarUInt((uint)(endOffset - startOffset));
            _prevStart = startOffset;
        }

        _positionsExpected--;
    }

    /// <summary>Completes the blob. <see cref="Blob"/> then holds its bytes.</summary>
    public void End()
    {
        CheckDocComplete();
        if (DocFreq == 0)
        {
            return;
        }

        FlushBlock();
        if (_entries.Count == 1)
        {
            _blob.WriteVarUInt((uint)_entries[0].LastDoc);
            _blob.WriteVarUInt((uint)_termMaxFreq);
            _blob.WriteVarUInt((uint)_termMinLen);
        }
        else
        {
            _blob.WriteVarUInt((uint)_termMaxFreq);
            _blob.WriteVarUInt((uint)_termMinLen);
            foreach ((int lastDoc, uint dataOffset, int maxFreq, int minLen) in _entries)
            {
                Span<byte> e = _blob.Reserve(16);
                BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)lastDoc);
                BinaryPrimitives.WriteUInt32LittleEndian(e[4..], dataOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)maxFreq);
                BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)minLen);
            }
        }

        _blob.Write(_data.Span);
    }

    private void CheckDocComplete()
    {
        if (_inDoc && _positionsExpected != 0)
        {
            throw new InvalidOperationException($"The document has {_positionsExpected} fewer positions than its frequency.");
        }
    }

    private void FlushBlock()
    {
        int n = _inBlock;
        if (n == 0)
        {
            return;
        }

        uint maxGap = 0;
        uint maxFreq = 0;
        for (int i = 0; i < n; i++)
        {
            maxGap = Math.Max(maxGap, _gaps[i]);
            maxFreq = Math.Max(maxFreq, _freqs[i]);
        }

        int dw = BitPacking.BitsRequired(maxGap);
        int fw = BitPacking.BitsRequired(maxFreq);
        _entries.Add((_lastDoc, (uint)_data.Length, _blockMaxFreq, _blockMinLen));
        _data.WriteByte((byte)dw);
        _data.WriteByte((byte)fw);
        BitPacking.Pack(_gaps.AsSpan(0, n), dw, _data.Reserve(BitPacking.PackedLength(n, dw)));
        BitPacking.Pack(_freqs.AsSpan(0, n), fw, _data.Reserve(BitPacking.PackedLength(n, fw)));
        _data.Write(_positions.Span);
        _positions.Clear();
        _termMaxFreq = Math.Max(_termMaxFreq, _blockMaxFreq);
        _termMinLen = Math.Min(_termMinLen, _blockMinLen);
        _blockLastDoc = _lastDoc;
        _blockMaxFreq = 0;
        _blockMinLen = int.MaxValue;
        _inBlock = 0;
    }
}
