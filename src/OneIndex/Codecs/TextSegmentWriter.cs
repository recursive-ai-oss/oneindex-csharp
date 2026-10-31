using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>The three files of a segment's text index as written, with the length and checksum to record in a commit.</summary>
/// <param name="Terms">The term dictionaries (part <c>terms</c>).</param>
/// <param name="Postings">The postings (part <c>postings</c>).</param>
/// <param name="Lengths">The per-document field lengths (part <c>lengths</c>).</param>
public readonly record struct TextSegmentFiles(IndexFileSummary Terms, IndexFileSummary Postings, IndexFileSummary Lengths);

/// <summary>
/// Where a text segment writer puts one of its files: it receives the body after the file header and completes the file (footer,
/// fsync). The segment index writer provides targets over its own file sink; <see cref="TextSegmentWriter"/> can also create plain
/// files in a storage itself.
/// </summary>
public interface ITextFileTarget : IDisposable
{
    /// <summary>The number of bytes written so far, header included (postings offsets are file offsets).</summary>
    long Position { get; }

    /// <summary>Appends bytes to the file body.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Appends the footer, fsyncs and closes the file.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The file's length and checksum, or <see langword="default"/> when the target records them itself.</returns>
    IndexFileSummary Complete(CancellationToken cancellationToken = default);

    /// <summary>The asynchronous twin of <see cref="Write"/>; by default it writes synchronously.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the bytes are written.</returns>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Write(data.Span, cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <summary>The asynchronous twin of <see cref="Complete"/>; by default it completes synchronously.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The file's length and checksum, or <see langword="default"/>.</returns>
    ValueTask<IndexFileSummary> CompleteAsync(CancellationToken cancellationToken = default) => new(Complete(cancellationToken));
}

internal sealed class StorageFileTarget(IndexFileWriter writer) : ITextFileTarget
{
    public long Position => writer.Position;

    public void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default) => writer.Write(data, cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => writer.WriteAsync(data, cancellationToken);

    public IndexFileSummary Complete(CancellationToken cancellationToken = default) => writer.Finish(cancellationToken);

    public ValueTask<IndexFileSummary> CompleteAsync(CancellationToken cancellationToken = default) => writer.FinishAsync(cancellationToken);

    public void Dispose() => writer.Dispose();
}

/// <summary>
/// Writes a segment's text index (see docs/term-dictionary.md and docs/postings.md): the term dictionary of every field, the
/// postings, and the field lengths. It is fed terms in byte order within each field and fields in ordinal name order, by a flush
/// from an <see cref="InvertedBuffer"/> or by <see cref="TextSegmentMerger"/>; the files come out the same whichever fed them.
/// Writes go through the storage interface, so a fault injector sees every one. Nothing here is durable until
/// <see cref="Finish"/> has fsynced the files and the caller has synced the directory and committed.
/// </summary>
public sealed class TextSegmentWriter : IDisposable, IAsyncDisposable
{
    internal static readonly uint TermsMagic = IndexFile.Magic("OIXT");
    internal static readonly uint PostingsMagic = IndexFile.Magic("OIXP");
    internal static readonly uint LengthsMagic = IndexFile.Magic("OIXL");

    private readonly ITextFileTarget _terms;
    private readonly ITextFileTarget _postings;
    private readonly ITextFileTarget _lengthsFile;
    private readonly IndexLimits _limits;
    private readonly PostingsBlobBuilder _blob = new();
    private readonly List<(string Name, int[] Lengths)> _lengths = [];
    private readonly ByteBuilder _scratch = new(1024);

    private string? _fieldName;
    private string? _lastFieldName;
    private int[] _fieldLengths = [];
    private bool _hasPositions;
    private bool _hasOffsets;
    private FstBuilder _fst;
    private TermInfoBuilder _info = new();
    private long _sumTotalTermFreq;
    private long _sumDocFreq;
    private int _maxTermBytes;
    private byte[] _term = new byte[32];
    private int _termLength;
    private bool _inTerm;
    private bool _finished;

    /// <summary>Opens the three files for writing; they are created now and must not exist.</summary>
    /// <param name="storage">Where to write.</param>
    /// <param name="termsFile">The name of the terms file.</param>
    /// <param name="postingsFile">The name of the postings file.</param>
    /// <param name="lengthsFile">The name of the lengths file.</param>
    /// <param name="maxDoc">One more than the largest document id of the segment.</param>
    /// <param name="limits">Limits; <see cref="IndexLimits.Default"/> when <see langword="null"/>.</param>
    public TextSegmentWriter(IStorage storage, string termsFile, string postingsFile, string lengthsFile, int maxDoc, IndexLimits? limits = null)
        : this(Targets(storage, termsFile, postingsFile, lengthsFile), maxDoc, limits)
    {
    }

    /// <summary>Writes through targets the caller provides (the segment index writer's file sink); the targets are disposed with the writer.</summary>
    /// <param name="terms">Target of the terms file, positioned after its header (magic <c>OIXT</c>, version 1).</param>
    /// <param name="postings">Target of the postings file (magic <c>OIXP</c>, version 1).</param>
    /// <param name="lengths">Target of the lengths file (magic <c>OIXL</c>, version 1).</param>
    /// <param name="maxDoc">One more than the largest document id of the segment.</param>
    /// <param name="limits">Limits; <see cref="IndexLimits.Default"/> when <see langword="null"/>.</param>
    public TextSegmentWriter(ITextFileTarget terms, ITextFileTarget postings, ITextFileTarget lengths, int maxDoc, IndexLimits? limits = null)
        : this((terms, postings, lengths), maxDoc, limits)
    {
    }

    private TextSegmentWriter((ITextFileTarget Terms, ITextFileTarget Postings, ITextFileTarget Lengths) targets, int maxDoc, IndexLimits? limits)
    {
        ArgumentNullException.ThrowIfNull(targets.Terms);
        ArgumentNullException.ThrowIfNull(targets.Postings);
        ArgumentNullException.ThrowIfNull(targets.Lengths);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDoc);
        _limits = limits ?? IndexLimits.Default;
        MaxDoc = maxDoc;
        _fst = new FstBuilder(_limits.MaxTermBytes);
        _terms = targets.Terms;
        _postings = targets.Postings;
        _lengthsFile = targets.Lengths;
        _scratch.WriteVarUInt((uint)maxDoc);
        _terms.Write(_scratch.Span);
    }

    private static (ITextFileTarget, ITextFileTarget, ITextFileTarget) Targets(IStorage storage, string termsFile, string postingsFile, string lengthsFile)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var terms = new StorageFileTarget(new IndexFileWriter(storage.Create(termsFile), TermsMagic, CodecRegistry.NewestVersion(SegmentParts.Terms)));
        try
        {
            var postings = new StorageFileTarget(new IndexFileWriter(storage.Create(postingsFile), PostingsMagic, CodecRegistry.NewestVersion(SegmentParts.Postings)));
            try
            {
                return (terms, postings, new StorageFileTarget(new IndexFileWriter(storage.Create(lengthsFile), LengthsMagic, CodecRegistry.NewestVersion(SegmentParts.Lengths))));
            }
            catch
            {
                postings.Dispose();
                throw;
            }
        }
        catch
        {
            terms.Dispose();
            throw;
        }
    }

    /// <summary>One more than the largest document id of the segment.</summary>
    public int MaxDoc { get; }

    /// <summary>Starts a field. Fields must come in increasing ordinal order of their names.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="features">What the field stores: <see cref="PostingsFeatures.Frequencies"/> (documents and frequencies), <see cref="PostingsFeatures.Positions"/> or <see cref="PostingsFeatures.Offsets"/>.</param>
    /// <param name="fieldLengths">The number of terms the field has in each document, <see cref="MaxDoc"/> entries (0 for a document without the field). Kept until <see cref="Finish"/>.</param>
    /// <exception cref="ArgumentException">The name does not follow the previous one, or the lengths array has the wrong size.</exception>
    public void BeginField(string name, PostingsFeatures features, int[] fieldLengths)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fieldLengths);
        ObjectDisposedException.ThrowIf(_finished, this);
        if (_fieldName is not null)
        {
            throw new InvalidOperationException("The previous field has not ended.");
        }

        if (_lastFieldName is not null && string.CompareOrdinal(name, _lastFieldName) <= 0)
        {
            throw new ArgumentException("Fields must be written in increasing ordinal name order.", nameof(name));
        }

        if (fieldLengths.Length != MaxDoc)
        {
            throw new ArgumentException($"Expected {MaxDoc} field lengths, got {fieldLengths.Length}.", nameof(fieldLengths));
        }

        _fieldName = name;
        _fieldLengths = fieldLengths;
        _hasOffsets = (features & PostingsFeatures.Offsets) == PostingsFeatures.Offsets;
        _hasPositions = _hasOffsets || (features & PostingsFeatures.Positions) == PostingsFeatures.Positions;
        _fst = new FstBuilder(_limits.MaxTermBytes);
        _info = new TermInfoBuilder();
        _sumTotalTermFreq = 0;
        _sumDocFreq = 0;
        _maxTermBytes = 0;
    }

    /// <summary>Starts a term. Terms must come in strictly increasing byte order within a field.</summary>
    /// <param name="term">The term in UTF-8, at most <see cref="IndexLimits.MaxTermBytes"/> bytes.</param>
    public void BeginTerm(ReadOnlySpan<byte> term)
    {
        if (_fieldName is null || _inTerm)
        {
            throw new InvalidOperationException("BeginTerm needs an open field and no open term.");
        }

        if (term.Length > _limits.MaxTermBytes)
        {
            throw new ArgumentException($"A term of {term.Length} bytes exceeds the limit of {_limits.MaxTermBytes}.", nameof(term));
        }

        if (_term.Length < term.Length)
        {
            _term = new byte[Math.Max(term.Length, _term.Length * 2)];
        }

        term.CopyTo(_term);
        _termLength = term.Length;
        _inTerm = true;
        _blob.Begin(_hasPositions, _hasOffsets, _fieldLengths, MaxDoc);
    }

    /// <summary>Adds a document to the open term. Documents must increase.</summary>
    /// <param name="docId">The segment-local document id, below <see cref="MaxDoc"/>.</param>
    /// <param name="freq">How many times the term occurs in it, at least 1.</param>
    public void AddDocument(int docId, int freq)
    {
        RequireTerm();
        _blob.AddDocument(docId, freq);
    }

    /// <summary>Adds the next occurrence of the term in the last added document; call it exactly the document's frequency times when the field has positions.</summary>
    /// <param name="position">The token position, not below the previous one.</param>
    /// <param name="startOffset">The character offset where the occurrence starts; stored only for a field with offsets.</param>
    /// <param name="endOffset">The offset where it ends, not below <paramref name="startOffset"/>.</param>
    public void AddPosition(int position, int startOffset = 0, int endOffset = 0)
    {
        RequireTerm();
        _blob.AddPosition(position, startOffset, endOffset);
    }

    /// <summary>Ends the open term and writes it.</summary>
    /// <returns><see langword="false"/> when no document was added, in which case the term is dropped (a merge can leave a term with no surviving document).</returns>
    public bool EndTerm()
    {
        RequireTerm();
        _inTerm = false;
        _blob.End();
        if (_blob.DocFreq == 0)
        {
            return false;
        }

        _fst.Add(_term.AsSpan(0, _termLength));
        long offset = _postings.Position;
        _postings.Write(_blob.Blob);
        _info.Add(_blob.DocFreq, _blob.TotalTermFreq, offset);
        _sumDocFreq += _blob.DocFreq;
        _sumTotalTermFreq += _blob.TotalTermFreq;
        _maxTermBytes = Math.Max(_maxTermBytes, _termLength);
        return true;
    }

    /// <summary>Ends the field. A field that got no terms is left out of the segment.</summary>
    public void EndField()
    {
        if (_fieldName is null || _inTerm)
        {
            throw new InvalidOperationException("EndField needs an open field and no open term.");
        }

        string name = _fieldName;
        _fieldName = null;
        _lastFieldName = name;
        if (_info.Count == 0)
        {
            return;
        }

        (byte[] fst, int root) = _fst.Finish();
        int docCount = 0;
        foreach (int len in _fieldLengths)
        {
            if (len < 0)
            {
                throw new ArgumentException("Field lengths cannot be negative.");
            }

            if (len > 0)
            {
                docCount++;
            }
        }

        _scratch.Clear();
        _scratch.WriteByte(1);
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        _scratch.WriteVarUInt((uint)nameBytes.Length);
        _scratch.Write(nameBytes);
        _scratch.WriteByte((byte)((_hasPositions ? 1 : 0) | (_hasOffsets ? 2 : 0)));
        _scratch.WriteVarUInt((uint)docCount);
        _scratch.WriteVarUInt((ulong)_sumTotalTermFreq);
        _scratch.WriteVarUInt((ulong)_sumDocFreq);
        _scratch.WriteVarUInt((ulong)_info.Count);
        _scratch.WriteVarUInt((ulong)_fst.TotalTermBytes);
        _scratch.WriteVarUInt((uint)_maxTermBytes);
        _scratch.WriteVarUInt((uint)fst.Length);
        _scratch.WriteVarUInt((uint)root);
        _terms.Write(_scratch.Span);
        _terms.Write(fst);
        _scratch.Clear();
        _scratch.WriteVarUInt((uint)_info.Table.Length);
        _scratch.Write(_info.Table);
        _scratch.WriteVarUInt((ulong)((_info.Count + TermInfoBuilder.GroupSize - 1) / TermInfoBuilder.GroupSize));
        _scratch.Write(_info.Samples);
        _terms.Write(_scratch.Span);
        _lengths.Add((name, _fieldLengths));
        _fieldLengths = [];
    }

    /// <summary>Completes and fsyncs the three files.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Each file's length and checksum.</returns>
    public TextSegmentFiles Finish(CancellationToken cancellationToken = default)
    {
        PrepareFinish();
        _terms.Write([0], cancellationToken);
        IndexFileSummary terms = _terms.Complete(cancellationToken);
        _lengthsFile.Write(BuildLengths(), cancellationToken);
        IndexFileSummary lengths = _lengthsFile.Complete(cancellationToken);
        IndexFileSummary postings = _postings.Complete(cancellationToken);
        _finished = true;
        return new TextSegmentFiles(terms, postings, lengths);
    }

    /// <summary>The asynchronous twin of <see cref="Finish"/>.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Each file's length and checksum.</returns>
    public async ValueTask<TextSegmentFiles> FinishAsync(CancellationToken cancellationToken = default)
    {
        PrepareFinish();
        await _terms.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
        IndexFileSummary terms = await _terms.CompleteAsync(cancellationToken).ConfigureAwait(false);
        await _lengthsFile.WriteAsync(BuildLengths(), cancellationToken).ConfigureAwait(false);
        IndexFileSummary lengths = await _lengthsFile.CompleteAsync(cancellationToken).ConfigureAwait(false);
        IndexFileSummary postings = await _postings.CompleteAsync(cancellationToken).ConfigureAwait(false);
        _finished = true;
        return new TextSegmentFiles(terms, postings, lengths);
    }

    /// <summary>Abandons the files if <see cref="Finish"/> did not complete; partial files are left for the caller to delete.</summary>
    public void Dispose()
    {
        _terms.Dispose();
        _postings.Dispose();
        _lengthsFile.Dispose();
    }

    /// <summary>Abandons the files if <see cref="FinishAsync"/> did not complete.</summary>
    /// <returns>A task that completes when the files are closed.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void PrepareFinish()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (_fieldName is not null)
        {
            throw new InvalidOperationException("A field is still open.");
        }
    }

    private void RequireTerm()
    {
        if (!_inTerm)
        {
            throw new InvalidOperationException("No term is open.");
        }
    }

    // The lengths file: maxDoc, then per field its name, the byte width of one length (1 to 4) and every document's length.
    private byte[] BuildLengths()
    {
        var b = new ByteBuilder(1024);
        b.WriteVarUInt((uint)MaxDoc);
        foreach ((string name, int[] lengths) in _lengths)
        {
            b.WriteByte(1);
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            b.WriteVarUInt((uint)nameBytes.Length);
            b.Write(nameBytes);
            int max = 0;
            foreach (int len in lengths)
            {
                max = Math.Max(max, len);
            }

            int width = max <= byte.MaxValue ? 1 : max <= ushort.MaxValue ? 2 : max <= 0xFFFFFF ? 3 : 4;
            b.WriteByte((byte)width);
            Span<byte> dest = b.Reserve(width * lengths.Length);
            for (int i = 0; i < lengths.Length; i++)
            {
                uint v = (uint)lengths[i];
                for (int k = 0; k < width; k++)
                {
                    dest[(i * width) + k] = (byte)(v >> (8 * k));
                }
            }
        }

        b.WriteByte(0);
        return b.Span.ToArray();
    }
}
