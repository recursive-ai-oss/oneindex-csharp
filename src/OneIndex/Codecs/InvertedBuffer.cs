using System.Text;
using OneIndex.Analysis;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>
/// The in-memory inverted index a segment is built from before it is flushed: per field, every term with the documents it occurs
/// in, its frequency there and, if the field keeps them, positions and character offsets; plus each document's field length (the
/// number of tokens the field has). Documents are added in increasing id order per field. Not thread safe: the writer keeps one per
/// indexing thread and flushes each into its own segment.
/// </summary>
public sealed class InvertedBuffer
{
    private readonly IndexLimits _limits;
    private readonly Dictionary<string, FieldBuffer> _fields = new(StringComparer.Ordinal);
    private byte[] _utf8 = new byte[256];

    /// <summary>Creates an empty buffer.</summary>
    /// <param name="limits">Limits; <see cref="IndexLimits.Default"/> when <see langword="null"/>.</param>
    public InvertedBuffer(IndexLimits? limits = null) => _limits = limits ?? IndexLimits.Default;

    /// <summary>The position gap left between the values of a multi-valued field, so a phrase cannot match across two values. Default 100.</summary>
    public int PositionGap { get; init; } = 100;

    /// <summary>A rough count of the memory the buffered postings hold, for deciding when to flush.</summary>
    public long ApproximateBytes { get; private set; }

    /// <summary>How many tokens were dropped because their term is longer than <see cref="IndexLimits.MaxTermBytes"/>.</summary>
    public long SkippedTokens { get; private set; }

    /// <summary>Whether nothing has been added.</summary>
    public bool IsEmpty => _fields.Count == 0;

    /// <summary>States what a field stores. A field that is never declared stores positions without offsets.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="features">Frequencies only, positions, or positions with offsets.</param>
    /// <exception cref="InvalidOperationException">The field already has tokens with different features.</exception>
    public void DeclareField(string field, PostingsFeatures features)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (_fields.TryGetValue(field, out FieldBuffer? existing))
        {
            if (existing.Features != features)
            {
                throw new InvalidOperationException($"Field '{field}' was already used with different features.");
            }

            return;
        }

        _fields.Add(field, new FieldBuffer(features));
    }

    /// <summary>Adds the tokens of one value of a field in a document. Call again for the next value of a multi-valued field.</summary>
    /// <param name="docId">The segment-local document id, not below the field's previous document.</param>
    /// <param name="field">The field name.</param>
    /// <param name="tokens">The analyzer's stream, started on the text; read to its end.</param>
    public void AddField(int docId, string field, TokenSource tokens)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(tokens);
        FieldBuffer f = GetField(field);
        int basePosition = f.StartValue(docId, PositionGap);
        int position = basePosition - 1;
        int last = basePosition - 1;
        while (tokens.MoveNext())
        {
            Token t = tokens.Token;
            position += t.PositionIncrement;
            int p = Math.Max(position, basePosition);
            ReadOnlySpan<char> term = t.Term;
            int max = Encoding.UTF8.GetMaxByteCount(term.Length);
            if (_utf8.Length < max)
            {
                _utf8 = new byte[Math.Max(max, _utf8.Length * 2)];
            }

            int n = Encoding.UTF8.GetBytes(term, _utf8);
            AddToken(f, docId, _utf8.AsSpan(0, n), p, t.StartOffset, t.EndOffset);
            last = Math.Max(last, p);
        }

        f.EndValue(last);
    }

    /// <summary>Adds one token. The low-level form of <see cref="AddField"/>: the caller supplies positions (not decreasing within a document).</summary>
    /// <param name="docId">The segment-local document id.</param>
    /// <param name="field">The field name.</param>
    /// <param name="term">The term in UTF-8.</param>
    /// <param name="position">The token position.</param>
    /// <param name="startOffset">The start character offset (stored only for a field with offsets).</param>
    /// <param name="endOffset">The end character offset.</param>
    public void AddToken(int docId, string field, ReadOnlySpan<byte> term, int position, int startOffset = 0, int endOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(field);
        FieldBuffer f = GetField(field);
        f.StartValue(docId, 0);
        AddToken(f, docId, term, position, startOffset, endOffset);
        f.NextPositionBase = Math.Max(f.NextPositionBase, position + 1);
    }

    /// <summary>Writes every field to <paramref name="writer"/> in the order it needs, ready for <see cref="TextSegmentWriter.Finish"/>. The buffer is left unchanged.</summary>
    /// <param name="writer">The segment writer; its <see cref="TextSegmentWriter.MaxDoc"/> must exceed every document id added.</param>
    /// <exception cref="ArgumentException">A document id is not below the writer's document count.</exception>
    public void WriteTo(TextSegmentWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var names = new List<string>(_fields.Keys);
        names.Sort(StringComparer.Ordinal);
        foreach (string name in names)
        {
            FieldBuffer f = _fields[name];
            if (f.Terms.Count == 0)
            {
                continue;
            }

            if (f.LastDoc >= writer.MaxDoc)
            {
                throw new ArgumentException($"Field '{name}' has document {f.LastDoc}, but the segment has {writer.MaxDoc} documents.", nameof(writer));
            }

            var lengths = new int[writer.MaxDoc];
            Array.Copy(f.Lengths, lengths, Math.Min(f.Lengths.Length, lengths.Length));
            writer.BeginField(name, f.Features, lengths);
            var terms = new List<TermBuffer>(f.Terms.Values);
            terms.Sort(static (a, b) => a.Term.AsSpan().SequenceCompareTo(b.Term));
            bool positions = (f.Features & PostingsFeatures.Positions) == PostingsFeatures.Positions;
            bool offsets = (f.Features & PostingsFeatures.Offsets) == PostingsFeatures.Offsets;
            foreach (TermBuffer t in terms)
            {
                writer.BeginTerm(t.Term);
                int[] d = t.Data;
                int i = 0;
                while (i < t.Length)
                {
                    int doc = d[i];
                    int freq = d[i + 1];
                    writer.AddDocument(doc, freq);
                    i += 2;
                    if (positions)
                    {
                        for (int k = 0; k < freq; k++)
                        {
                            if (offsets)
                            {
                                writer.AddPosition(d[i], d[i + 1], d[i + 2]);
                                i += 3;
                            }
                            else
                            {
                                writer.AddPosition(d[i]);
                                i++;
                            }
                        }
                    }
                }

                writer.EndTerm();
            }

            writer.EndField();
        }
    }

    private FieldBuffer GetField(string field)
    {
        if (!_fields.TryGetValue(field, out FieldBuffer? f))
        {
            f = new FieldBuffer(PostingsFeatures.Positions);
            _fields.Add(field, f);
        }

        return f;
    }

    private void AddToken(FieldBuffer f, int docId, ReadOnlySpan<byte> term, int position, int startOffset, int endOffset)
    {
        if (term.Length > _limits.MaxTermBytes)
        {
            SkippedTokens++;
            return;
        }

        if (!f.TermLookup.TryGetValue(term, out TermBuffer? t))
        {
            t = new TermBuffer(term.ToArray());
            f.Terms.Add(t.Term, t);
            ApproximateBytes += 96 + term.Length;
        }

        int before = t.Data.Length;
        t.Append(docId, position, startOffset, endOffset, f.Features);
        ApproximateBytes += (long)(t.Data.Length - before) * sizeof(int);
        f.AddLength(docId);
        if (f.Lengths.Length > f.LengthsReported)
        {
            ApproximateBytes += (long)(f.Lengths.Length - f.LengthsReported) * sizeof(int);
            f.LengthsReported = f.Lengths.Length;
        }
    }

    private sealed class FieldBuffer
    {
        public FieldBuffer(PostingsFeatures features)
        {
            Features = features;
            TermLookup = Terms.GetAlternateLookup<ReadOnlySpan<byte>>();
        }

        public PostingsFeatures Features { get; }

        public Dictionary<byte[], TermBuffer> Terms { get; } = new(FstBuilder.ByteArrayComparer.Instance);

        public Dictionary<byte[], TermBuffer>.AlternateLookup<ReadOnlySpan<byte>> TermLookup { get; }

        public int[] Lengths => _lengths;

        public int LengthsReported { get; set; }

        public int LastDoc { get; private set; } = -1;

        public int NextPositionBase { get; set; }

        /// <summary>Called when a value starts; returns where its positions begin.</summary>
        public int StartValue(int docId, int gap)
        {
            if (docId < 0 || docId < LastDoc)
            {
                throw new ArgumentOutOfRangeException(nameof(docId), $"Document {docId} comes before document {LastDoc}; documents must be added in increasing order per field.");
            }

            if (docId != LastDoc)
            {
                LastDoc = docId;
                NextPositionBase = 0;
                return 0;
            }

            return NextPositionBase + gap;
        }

        public void EndValue(int lastPosition) => NextPositionBase = Math.Max(NextPositionBase, lastPosition + 1);

        public void AddLength(int docId)
        {
            if (docId >= _lengths.Length)
            {
                Array.Resize(ref _lengths, Math.Max(docId + 1, Math.Max(16, _lengths.Length * 2)));
            }

            _lengths[docId]++;
        }

        private int[] _lengths = [];
    }

    private sealed class TermBuffer(byte[] term)
    {
        public byte[] Term { get; } = term;

        public int[] Data = new int[8];
        public int Length;
        private int _lastDoc = -1;
        private int _freqIndex;

        public void Append(int docId, int position, int startOffset, int endOffset, PostingsFeatures features)
        {
            bool positions = (features & PostingsFeatures.Positions) == PostingsFeatures.Positions;
            bool offsets = (features & PostingsFeatures.Offsets) == PostingsFeatures.Offsets;
            int need = (_lastDoc == docId ? 0 : 2) + (positions ? (offsets ? 3 : 1) : 0);
            if (Data.Length - Length < need)
            {
                Array.Resize(ref Data, Math.Max(Data.Length * 2, Length + need));
            }

            if (_lastDoc != docId)
            {
                _lastDoc = docId;
                Data[Length] = docId;
                _freqIndex = Length + 1;
                Data[_freqIndex] = 0;
                Length += 2;
            }

            Data[_freqIndex]++;
            if (positions)
            {
                Data[Length++] = position;
                if (offsets)
                {
                    Data[Length++] = startOffset;
                    Data[Length++] = endOffset;
                }
            }
        }
    }
}
