using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Intrinsics;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>
/// Reads one term's postings from a postings file, block by block, straight from storage ranges: the skip entries it needs and
/// the block it is in, nothing else. After <see cref="Reset"/> an instance owns all its buffers, so iterating, advancing and
/// reading positions allocate nothing, and one instance can be pointed at another term with <see cref="Reset"/> to reuse them.
/// Every length, offset and document id read is checked; a violation raises <see cref="CorruptIndexException"/> naming the file.
/// </summary>
public sealed class TermPostings : PostingsIterator
{
    private const int BlockSize = PostingsBlobBuilder.BlockSize;
    private const int EntryBytes = 16;
    private const int PositionBufferBytes = 2048;
    private const int MaxHeaderBytes = 15;

    private readonly IRangeReader _file;
    private readonly long _dataEnd;
    private readonly int _maxDoc;
    private readonly bool _fieldHasPositions;
    private readonly bool _fieldHasOffsets;
    private readonly int[] _docs = new int[BlockSize];
    private readonly int[] _freqs = new int[BlockSize];
    private readonly uint[] _unpacked = new uint[BlockSize];
    // Room for a one-block term's header (at most 15 bytes) and its whole block, read in one go by Reset.
    private readonly byte[] _scratch = new byte[MaxHeaderBytes + 2 + (8 * BlockSize)];
    private readonly byte[] _posBuffer = new byte[PositionBufferBytes];

    // Lists with many blocks that are skipped through (phrases, conjunctions) cache the last document of every block.
    private const int LastDocCacheMinBlocks = 4;
    private const int LastDocCacheAfterReads = 6;
    private int[]? _lastDocs;
    private int _lastDocReads;
    private int _preloadedBlockAt = -1;
    private int _preloadedEnd;
    private bool _wantFreq;
    private bool _wantPos;
    private bool _wantOffsets;
    private int _docFreq;
    private int _blockCount;
    private long _tableStart;
    private long _blocksStart;
    private BlockImpact _termImpact;
    private int _singleLastDoc;
    private int _blockImpactMaxFreq;
    private int _blockImpactMinLen;

    private int _block = -1;
    private int _blockN;
    private int _blockLastDoc;
    private int _index;
    private int _doc = -1;

    private long _posNextLoad;
    private int _posBufPos;
    private int _posBufLen;
    private long _posAhead;
    private int _posRead;
    private int _prevPosition;
    private int _prevStart;
    private int _startOffset;
    private int _endOffset;
    private long _posBlockStart;

    internal TermPostings(IRangeReader file, int maxDoc, bool hasPositions, bool hasOffsets)
    {
        _file = file;
        _dataEnd = file.Length - IndexFile.FooterLength;
        _maxDoc = maxDoc;
        _fieldHasPositions = hasPositions;
        _fieldHasOffsets = hasOffsets;
        _doc = NoMoreDocs;
    }

    /// <inheritdoc/>
    public override int DocId => _doc;

    /// <inheritdoc/>
    public override long Cost => _docFreq;

    /// <inheritdoc/>
    public override int Frequency => _wantFreq ? _freqs[_index] : 1;

    /// <inheritdoc/>
    public override int StartOffset => _startOffset;

    /// <inheritdoc/>
    public override int EndOffset => _endOffset;

    /// <inheritdoc/>
    public override BlockImpact TermImpact => _termImpact;

    /// <summary>Points the instance at a term's postings, positioned before the first document.</summary>
    /// <param name="term">The term as returned by the dictionary.</param>
    /// <param name="features">What the caller will read; must be a subset of what the field was indexed with.</param>
    /// <exception cref="ArgumentException">More was requested than the field stores (positions or offsets it was indexed without).</exception>
    /// <exception cref="CorruptIndexException">The term's statistics or postings header are invalid.</exception>
    public void Reset(in TermInfo term, PostingsFeatures features)
    {
        _wantFreq = (features & PostingsFeatures.Frequencies) != 0;
        _wantPos = (features & PostingsFeatures.Positions) == PostingsFeatures.Positions;
        _wantOffsets = (features & PostingsFeatures.Offsets) == PostingsFeatures.Offsets;
        if ((_wantPos && !_fieldHasPositions) || (_wantOffsets && !_fieldHasOffsets))
        {
            throw new ArgumentException("The field was not indexed with the requested positions or offsets.", nameof(features));
        }

        if (term.DocFreq < 1 || term.DocFreq > _maxDoc || term.TotalTermFreq < term.DocFreq || term.PostingsOffset < IndexFile.HeaderLength || term.PostingsOffset >= _dataEnd)
        {
            throw Corrupt($"term statistics are invalid (docFreq {term.DocFreq}, offset {term.PostingsOffset})");
        }

        _docFreq = term.DocFreq;
        _blockCount = (_docFreq + BlockSize - 1) / BlockSize;
        // A term of one block (the common case for rare terms) is read with its header in a single call.
        long available = _dataEnd - term.PostingsOffset;
        int want = (int)Math.Min(_blockCount == 1 ? MaxHeaderBytes + 2 + (8L * _docFreq) : MaxHeaderBytes, available);
        _file.ReadExactly(term.PostingsOffset, _scratch.AsSpan(0, want));
        Span<byte> head = _scratch.AsSpan(0, Math.Min(MaxHeaderBytes, want));
        _preloadedBlockAt = -1;
        _lastDocs = null;
        _lastDocReads = 0;
        int pos = 0;
        if (_blockCount == 1)
        {
            long last = ReadVar(head, ref pos);
            long maxFreq = ReadVar(head, ref pos);
            long minLen = ReadVar(head, ref pos);
            if (last >= _maxDoc || maxFreq < 1 || maxFreq > int.MaxValue || minLen > int.MaxValue)
            {
                throw Corrupt("postings header is invalid");
            }

            _singleLastDoc = (int)last;
            _termImpact = new BlockImpact((int)last, (int)maxFreq, (int)minLen);
            _blocksStart = term.PostingsOffset + pos;
            _tableStart = 0;
            _preloadedBlockAt = pos; // the block starts here in the scratch buffer, 'want' bytes in all
            _preloadedEnd = want;
        }
        else
        {
            long maxFreq = ReadVar(head, ref pos);
            long minLen = ReadVar(head, ref pos);
            if (maxFreq < 1 || maxFreq > int.MaxValue || minLen > int.MaxValue)
            {
                throw Corrupt("postings header is invalid");
            }

            _tableStart = term.PostingsOffset + pos;
            _blocksStart = _tableStart + ((long)_blockCount * EntryBytes);
            if (_blocksStart >= _dataEnd)
            {
                throw Corrupt("skip table runs past the end of the file");
            }

            // The term's last document is the last skip entry's.
            _termImpact = new BlockImpact(ReadLastDoc(_blockCount - 1), (int)maxFreq, (int)minLen);
        }

        _block = -1;
        _blockN = 0;
        _index = 0;
        _doc = -1;
        _posAhead = 0;
        _posRead = 0;
        _posBufPos = 0;
        _posBufLen = 0;
        _fresh = false;
    }

    /// <inheritdoc/>
    public override int NextDoc()
    {
        if (_doc == NoMoreDocs)
        {
            return NoMoreDocs;
        }

        if (_block < 0)
        {
            LoadBlock(0);
            return MoveTo(0);
        }

        if (_index + 1 < _blockN)
        {
            return MoveTo(_index + 1);
        }

        if (_block + 1 >= _blockCount)
        {
            return Exhaust();
        }

        LoadBlock(_block + 1);
        return MoveTo(0);
    }

    /// <inheritdoc/>
    public override int Advance(int target)
    {
        if (_doc == NoMoreDocs)
        {
            return NoMoreDocs;
        }

        if (target <= _doc)
        {
            target = _doc + 1;
        }

        if (_block >= 0 && _blockLastDoc >= target)
        {
            return MoveTo(LowerBound(_index + 1, target));
        }

        int b = FindBlock(target, _block < 0 ? 0 : _block + 1);
        if (b >= _blockCount)
        {
            return Exhaust();
        }

        LoadBlock(b);
        return MoveTo(LowerBound(0, target));
    }

    /// <inheritdoc/>
    public override BlockImpact ShallowAdvance(int target)
    {
        if (_doc == NoMoreDocs || target > _termImpact.LastDocId)
        {
            return new BlockImpact(NoMoreDocs, 0, 0);
        }

        if (_blockCount == 1)
        {
            return _termImpact;
        }

        int b;
        if (_block >= 0 && _blockLastDoc >= target)
        {
            b = _block;
        }
        else
        {
            b = FindBlock(target, _block < 0 ? 0 : _block);
            if (b >= _blockCount)
            {
                return new BlockImpact(NoMoreDocs, 0, 0);
            }
        }

        Span<byte> entry = _scratch.AsSpan(0, EntryBytes);
        _file.ReadExactly(_tableStart + ((long)b * EntryBytes), entry);
        return new BlockImpact(CheckDoc(BinaryPrimitives.ReadUInt32LittleEndian(entry)), CheckInt(BinaryPrimitives.ReadUInt32LittleEndian(entry[8..])), CheckInt(BinaryPrimitives.ReadUInt32LittleEndian(entry[12..])));
    }

    /// <inheritdoc/>
    public override int NextPosition()
    {
        if (!_wantPos)
        {
            throw new InvalidOperationException("Positions were not requested.");
        }

        if (_doc < 0 || _doc == NoMoreDocs || _posRead >= _freqs[_index])
        {
            throw new InvalidOperationException("No position is left in the current document.");
        }

        if (_posAhead > 0)
        {
            SkipPositions(_posAhead);
            _posAhead = 0;
        }

        long position = _prevPosition + (long)ReadPosVar();
        if (position > int.MaxValue)
        {
            throw Corrupt("position overflows");
        }

        _prevPosition = (int)position;
        if (_wantOffsets)
        {
            uint z = ReadPosVar();
            long delta = (long)(z >> 1) ^ -(long)(z & 1);
            long start = _prevStart + delta;
            long length = ReadPosVar();
            if (start < 0 || start + length > int.MaxValue)
            {
                throw Corrupt("offset is out of range");
            }

            _prevStart = (int)start;
            _startOffset = _prevStart;
            _endOffset = (int)(start + length);
        }
        else if (_fieldHasOffsets)
        {
            // The caller does not want offsets, but they are stored: step over them.
            ReadPosVar();
            ReadPosVar();
        }

        _posRead++;
        return _prevPosition;
    }

    private int MoveTo(int index)
    {
        if (_wantPos)
        {
            // Positions of the documents passed over (and the unread rest of the one we leave) stay in the stream until needed.
            if (_fresh)
            {
                _posAhead = 0;
                for (int i = 0; i < index; i++)
                {
                    _posAhead += _freqs[i];
                }
            }
            else
            {
                _posAhead += _freqs[_index] - _posRead;
                for (int i = _index + 1; i < index; i++)
                {
                    _posAhead += _freqs[i];
                }
            }

            _posRead = 0;
            _prevPosition = 0;
            _prevStart = 0;
        }

        _fresh = false;
        _index = index;
        _doc = _docs[index];
        return _doc;
    }

    private bool _fresh;

    private int Exhaust()
    {
        _doc = NoMoreDocs;
        return NoMoreDocs;
    }

    private int LowerBound(int from, int target)
    {
        int lo = from;
        int hi = _blockN - 1;
        // The caller guarantees the block's last document is at least the target, so the answer exists.
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_docs[mid] >= target)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }

    private int ReadLastDoc(int block)
    {
        if (_lastDocs is not null)
        {
            return _lastDocs[block];
        }

        if (_blockCount > LastDocCacheMinBlocks && ++_lastDocReads > LastDocCacheAfterReads)
        {
            // Skipping through a long list reads many entries of the skip table: read the table once instead of entry by entry.
            LoadLastDocs();
            return _lastDocs![block];
        }

        Span<byte> four = _scratch.AsSpan(0, 4);
        _file.ReadExactly(_tableStart + ((long)block * EntryBytes), four);
        return CheckDoc(BinaryPrimitives.ReadUInt32LittleEndian(four));
    }

    private void LoadLastDocs()
    {
        var lastDocs = new int[_blockCount];
        const int EntriesPerRead = 64; // 1,024 bytes, within the scratch buffer
        for (int first = 0; first < _blockCount; first += EntriesPerRead)
        {
            int n = Math.Min(EntriesPerRead, _blockCount - first);
            Span<byte> chunk = _scratch.AsSpan(0, n * EntryBytes);
            _file.ReadExactly(_tableStart + ((long)first * EntryBytes), chunk);
            for (int i = 0; i < n; i++)
            {
                lastDocs[first + i] = CheckDoc(BinaryPrimitives.ReadUInt32LittleEndian(chunk[(i * EntryBytes)..]));
            }
        }

        _lastDocs = lastDocs;
    }

    // The first block at or after 'from' whose last document is at least 'target': gallop forward, then binary search.
    private int FindBlock(int target, int from)
    {
        if (_blockCount == 1)
        {
            return from == 0 && _singleLastDoc >= target ? 0 : 1;
        }

        if (from >= _blockCount)
        {
            return _blockCount;
        }

        if (ReadLastDoc(from) >= target)
        {
            return from;
        }

        int lo = from;
        int step = 1;
        int hi;
        while (true)
        {
            hi = lo + step;
            if (hi >= _blockCount)
            {
                hi = _blockCount - 1;
                if (ReadLastDoc(hi) < target)
                {
                    return _blockCount;
                }

                break;
            }

            if (ReadLastDoc(hi) >= target)
            {
                break;
            }

            lo = hi;
            step <<= 1;
        }

        while (hi - lo > 1)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (ReadLastDoc(mid) >= target)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return hi;
    }

    private void LoadBlock(int block)
    {
        int n = block < _blockCount - 1 ? BlockSize : _docFreq - (BlockSize * (_blockCount - 1));

        long blockStart;
        int previousLast;
        int lastDoc;
        if (_blockCount == 1)
        {
            blockStart = _blocksStart;
            previousLast = -1;
            lastDoc = _singleLastDoc;
            _blockImpactMaxFreq = _termImpact.MaxFrequency;
            _blockImpactMinLen = _termImpact.MinFieldLength;
        }
        else
        {
            // One read covers the previous entry (for the first document's base) and this one.
            int first = block > 0 ? block - 1 : block;
            int count = block > 0 ? 2 : 1;
            Span<byte> entries = _scratch.AsSpan(0, count * EntryBytes);
            _file.ReadExactly(_tableStart + ((long)first * EntryBytes), entries);
            ReadOnlySpan<byte> mine = entries[((count - 1) * EntryBytes)..];
            previousLast = block > 0 ? CheckDoc(BinaryPrimitives.ReadUInt32LittleEndian(entries)) : -1;
            lastDoc = CheckDoc(BinaryPrimitives.ReadUInt32LittleEndian(mine));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(mine[4..]);
            blockStart = _blocksStart + offset;
            _blockImpactMaxFreq = CheckInt(BinaryPrimitives.ReadUInt32LittleEndian(mine[8..]));
            _blockImpactMinLen = CheckInt(BinaryPrimitives.ReadUInt32LittleEndian(mine[12..]));
        }

        if (blockStart >= _dataEnd || lastDoc <= previousLast || lastDoc - previousLast < n)
        {
            throw Corrupt($"block {block} has an invalid position or last document");
        }

        int bound = (int)Math.Min(2 + (8L * n), _dataEnd - blockStart);
        if (bound < 2)
        {
            throw Corrupt("block runs past the end of the file");
        }

        Span<byte> data;
        if (_preloadedBlockAt >= 0 && _blockCount == 1 && _preloadedBlockAt + bound <= _preloadedEnd)
        {
            data = _scratch.AsSpan(_preloadedBlockAt, bound);
        }
        else
        {
            data = _scratch.AsSpan(0, bound);
            _file.ReadExactly(blockStart, data);
        }

        int dw = data[0];
        int fw = data[1];
        if (dw > 32 || fw > 32)
        {
            throw Corrupt("block uses an impossible bit width");
        }

        int dBytes = BitPacking.PackedLength(n, dw);
        int fBytes = BitPacking.PackedLength(n, fw);
        if (2 + dBytes + fBytes > bound)
        {
            throw Corrupt("block runs past the end of the file");
        }

        Span<uint> tmp = _unpacked.AsSpan(0, n);
        BitPacking.Unpack(data.Slice(2, dBytes), dw, tmp);
        long doc = previousLast;
        for (int i = 0; i < n; i++)
        {
            doc += (long)tmp[i] + 1;
            if (doc > lastDoc)
            {
                throw Corrupt("block documents pass the block's last document");
            }

            _docs[i] = (int)doc;
        }

        if (doc != lastDoc)
        {
            throw Corrupt("block's documents do not end at its last document");
        }

        if (_wantFreq)
        {
            BitPacking.Unpack(data.Slice(2 + dBytes, fBytes), fw, tmp);
            for (int i = 0; i < n; i++)
            {
                if (tmp[i] >= int.MaxValue)
                {
                    throw Corrupt("frequency overflows");
                }

                _freqs[i] = (int)tmp[i] + 1;
            }
        }

        _block = block;
        _blockN = n;
        _blockLastDoc = lastDoc;
        _posBlockStart = blockStart + 2 + dBytes + fBytes;
        _posNextLoad = _posBlockStart;
        _posBufPos = 0;
        _posBufLen = 0;
        _fresh = true;
    }

    private int CheckDoc(uint value)
    {
        if (value >= (uint)_maxDoc)
        {
            throw Corrupt("a document id is past the segment's document count");
        }

        return (int)value;
    }

    private int CheckInt(uint value)
    {
        if (value > int.MaxValue)
        {
            throw Corrupt("an impact value is out of range");
        }

        return (int)value;
    }

    private long ReadVar(ReadOnlySpan<byte> span, ref int pos)
    {
        long v = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            if (pos >= span.Length)
            {
                throw Corrupt("postings header is cut short");
            }

            byte b = span[pos++];
            v |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return v;
            }
        }

        throw Corrupt("postings header number is too long");
    }

    // Position stream: a small window over the file, refilled when fewer than five bytes (one varint) remain.
    private uint ReadPosVar()
    {
        if (_posBufLen - _posBufPos < 5)
        {
            RefillPositions();
        }

        uint v = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            if (_posBufPos >= _posBufLen)
            {
                throw Corrupt("position data ends inside a number");
            }

            byte b = _posBuffer[_posBufPos++];
            if (shift == 28 && b > 0x0F)
            {
                throw Corrupt("position number overflows 32 bits");
            }

            v |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return v;
            }
        }

        throw Corrupt("position number is too long");
    }

    private void SkipPositions(long count)
    {
        long remaining = count * (_fieldHasOffsets ? 3 : 1);
        if (!Vector128.IsHardwareAccelerated)
        {
            for (long i = 0; i < remaining; i++)
            {
                ReadPosVar();
            }

            return;
        }

        // A varint ends at the byte whose high bit is clear, so skipping n numbers means passing the n-th such byte. The window is
        // scanned 16 bytes at a time: the high bits of a chunk come out as a 16-bit mask whose zero bits mark the ends. The
        // skipped numbers are not decoded (the ones read afterwards are checked); running out of data still raises the corruption error.
        while (remaining > 0)
        {
            if (_posBufLen - _posBufPos < 16)
            {
                RefillPositions();
                if (_posBufLen - _posBufPos < 16)
                {
                    // The last bytes of the file: finish with the checked reader.
                    for (; remaining > 0; remaining--)
                    {
                        ReadPosVar();
                    }

                    return;
                }
            }

            uint ends = ~Vector128.Create(new ReadOnlySpan<byte>(_posBuffer, _posBufPos, 16)).ExtractMostSignificantBits() & 0xFFFFu;
            int found = BitOperations.PopCount(ends);
            if (found < remaining)
            {
                remaining -= found;
                _posBufPos += 16;
                continue;
            }

            for (long k = 1; k < remaining; k++)
            {
                ends &= ends - 1; // drop the lowest end
            }

            _posBufPos += BitOperations.TrailingZeroCount(ends) + 1;
            remaining = 0;
        }
    }

    private void RefillPositions()
    {
        int remaining = _posBufLen - _posBufPos;
        if (remaining > 0)
        {
            Buffer.BlockCopy(_posBuffer, _posBufPos, _posBuffer, 0, remaining);
        }

        long available = _dataEnd - _posNextLoad;
        int want = (int)Math.Min(_posBuffer.Length - remaining, available);
        if (want > 0)
        {
            _file.ReadExactly(_posNextLoad, _posBuffer.AsSpan(remaining, want));
            _posNextLoad += want;
        }

        _posBufPos = 0;
        _posBufLen = remaining + Math.Max(want, 0);
    }

    private CorruptIndexException Corrupt(string message) => new(_file.Name, "postings: " + message);
}
