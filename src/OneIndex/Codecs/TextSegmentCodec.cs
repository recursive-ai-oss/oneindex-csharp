using OneIndex.Analysis;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>
/// The text codec of the segment index writer: it owns the <c>terms</c>, <c>postings</c> and <c>lengths</c> parts. A flush analyzes
/// every indexed text and keyword field of the buffered documents into an <see cref="InvertedBuffer"/> and writes it; a merge
/// feeds <see cref="TextSegmentMerger"/> the sources' readers through their document maps; opening a segment gives a part reader
/// that implements <see cref="ITextFieldSource"/>.
/// </summary>
/// <remarks>
/// A field is indexed when its <see cref="FieldDefinition.Indexed"/> flag is set and its <see cref="FieldDefinition.IndexOptions"/> is
/// not <see cref="IndexOptions.None"/>; text fields use the analyzer named in the schema (the default analyzer when none is named),
/// keyword fields the keyword analyzer. Docs and frequency-only options store frequencies; positions and offsets as named.
/// </remarks>
public sealed class TextSegmentCodec : ISegmentCodec
{
    /// <inheritdoc/>
    public string Name => "text";

    /// <summary>
    /// Whether numeric, date and boolean fields are also written as terms (<see cref="NumericTerms"/>). Default <see langword="false"/>: the point
    /// index answers ranges, and terms for every number only add index size. Turn it on for an index written without the point codec, so
    /// range queries still find documents.
    /// </summary>
    public bool IndexNumericTerms { get; init; }


    /// <inheritdoc/>
    public IReadOnlyList<string> Parts { get; } = [SegmentParts.Terms, SegmentParts.Postings, SegmentParts.Lengths];

    /// <inheritdoc/>
    public void Flush(SegmentFlushContext context, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        var buffer = new InvertedBuffer(context.Limits);
        var analyzers = new Dictionary<string, Analyzer>(StringComparer.Ordinal);
        var numeric = new Dictionary<string, FieldType>(StringComparer.Ordinal);
        var sparse = new HashSet<string>(StringComparer.Ordinal);
        foreach (FieldDefinition f in context.Schema.Fields)
        {
            if (IsTextIndexed(f))
            {
                buffer.DeclareField(f.Name, FeaturesOf(f));
                analyzers[f.Name] = CreateAnalyzer(f);
            }
            else if (f.Type == FieldType.Sparse && f.Indexed)
            {
                // Sparse weights are postings frequencies (docs/sparse.md).
                buffer.DeclareField(f.Name, PostingsFeatures.Frequencies);
                sparse.Add(f.Name);
            }
            else if (IndexNumericTerms && NumericTerms.IsNumeric(f.Type) && f.Indexed)
            {
                // Numbers are terms too, so range queries work without a point index (see NumericTerms).
                buffer.DeclareField(f.Name, PostingsFeatures.Frequencies);
                numeric[f.Name] = f.Type;
            }
        }

        if (analyzers.Count == 0 && numeric.Count == 0 && sparse.Count == 0)
        {
            return;
        }

        Span<byte> numericTerm = stackalloc byte[NumericTerms.TermLength]; // fixed buffer, one numeric term

        for (int d = 0; d < context.Documents.Count; d++)
        {
            if ((d & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            foreach (DocumentField field in context.Documents[d].Fields)
            {
                if (analyzers.TryGetValue(field.Name, out Analyzer? analyzer) && field.Value.Kind == FieldValueKind.Text)
                {
                    buffer.AddField(d, field.Name, analyzer.GetTokens(field.Value.AsText().AsMemory()));
                }
                else if (field.Value.Kind == FieldValueKind.Sparse && sparse.Contains(field.Name))
                {
                    SparseVector sv = field.Value.AsSparse();
                    for (int k = 0; k < sv.Count; k++)
                    {
                        int q = SparseVector.Quantize(sv.Weights[k]);
                        if (q > 0)
                        {
                            buffer.AddWeightedTerm(d, field.Name, System.Text.Encoding.UTF8.GetBytes(sv.Tokens[k]), q);
                        }
                    }
                }
                else if (numeric.TryGetValue(field.Name, out FieldType type))
                {
                    NumericTerms.Write(NumericTerms.Ordered(type, field.Value), numericTerm);
                    buffer.AddToken(d, field.Name, numericTerm, 0);
                }
            }
        }

        using TextSegmentWriter writer = CreateWriter(sink, context.Documents.Count, context.Limits, cancellationToken);
        buffer.WriteTo(writer);
        writer.Finish(cancellationToken);
    }

    /// <inheritdoc/>
    public void Merge(SegmentMergeContext context, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        var sources = new List<TextMergeSource>();
        foreach (OneIndex.Index.MergeSource source in context.Sources)
        {
            if (source.Reader is PartReader part)
            {
                sources.Add(new TextMergeSource(part.Segment, [.. Enumerable.Range(0, source.MaxDoc).Select(d => source.DocMap[d])]));
            }
        }

        if (sources.Count == 0)
        {
            return;
        }

        using TextSegmentWriter writer = CreateWriter(sink, context.DocumentCount, context.Limits, cancellationToken);
        TextSegmentMerger.Merge(writer, sources, cancellationToken);
        writer.Finish(cancellationToken);
    }

    /// <inheritdoc/>
    public ISegmentPartReader? OpenReader(SegmentReaderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IRangeReader? terms = context.OpenPart(SegmentParts.Terms, out _, cancellationToken);
        if (terms is null)
        {
            return null;
        }

        IRangeReader? postings = null;
        IRangeReader? lengths = null;
        try
        {
            postings = context.OpenPart(SegmentParts.Postings, out _, cancellationToken)
                ?? throw new CorruptIndexException(terms.Name, "the segment has terms but no postings");
            lengths = context.OpenPart(SegmentParts.Lengths, out _, cancellationToken)
                ?? throw new CorruptIndexException(terms.Name, "the segment has terms but no field lengths");
        }
        catch
        {
            terms.Dispose();
            postings?.Dispose();
            lengths?.Dispose();
            throw;
        }

        TextSegmentReader segment = TextSegmentReader.Open(terms, postings, lengths, context.Limits, cancellationToken);
        if (segment.MaxDoc != context.Info.DocCount)
        {
            int found = segment.MaxDoc;
            segment.Dispose();
            throw new CorruptIndexException(context.Info.FindPart(SegmentParts.Terms)!.FileName, $"the text index covers {found} documents, the commit says {context.Info.DocCount}");
        }

        return new PartReader(segment);
    }

    private static TextSegmentWriter CreateWriter(SegmentFileSink sink, int maxDoc, IndexLimits limits, CancellationToken cancellationToken)
    {
        SegmentFileBuilder? terms = null;
        SegmentFileBuilder? postings = null;
        try
        {
            terms = sink.Create(SegmentParts.Terms, "OIXT", CodecRegistry.NewestVersion(SegmentParts.Terms), cancellationToken);
            postings = sink.Create(SegmentParts.Postings, "OIXP", CodecRegistry.NewestVersion(SegmentParts.Postings), cancellationToken);
            SegmentFileBuilder lengths = sink.Create(SegmentParts.Lengths, "OIXL", CodecRegistry.NewestVersion(SegmentParts.Lengths), cancellationToken);
            return new TextSegmentWriter(new BuilderTarget(terms), new BuilderTarget(postings), new BuilderTarget(lengths), maxDoc, limits);
        }
        catch
        {
            terms?.Dispose();
            postings?.Dispose();
            throw;
        }
    }

    // A keyword field is searchable by term whenever it is indexed (it has no positions to choose); a text field also needs index options.
    private static bool IsTextIndexed(FieldDefinition f)
        => f.Indexed && (f.Type == FieldType.Keyword || (f.Type == FieldType.Text && f.IndexOptions != IndexOptions.None));

    private static PostingsFeatures FeaturesOf(FieldDefinition f) => f.IndexOptions switch
    {
        IndexOptions.Offsets => PostingsFeatures.Offsets,
        IndexOptions.Positions => PostingsFeatures.Positions,
        _ => PostingsFeatures.Frequencies,
    };

    private static Analyzer CreateAnalyzer(FieldDefinition f)
        => f.Type == FieldType.Keyword ? AnalyzerRegistry.Create(KeywordAnalyzer.RegisteredName) : AnalyzerRegistry.Create(f.Analyzer ?? FieldDefinition.DefaultAnalyzer);

    private sealed class BuilderTarget(SegmentFileBuilder builder) : ITextFileTarget
    {
        public long Position => builder.Position;

        public void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default) => builder.Write(data, cancellationToken);

        public IndexFileSummary Complete(CancellationToken cancellationToken = default)
        {
            builder.Complete(cancellationToken);
            return default;
        }

        public void Dispose() => builder.Dispose();
    }

    private sealed class PartReader(TextSegmentReader segment) : ISegmentPartReader, ITextFieldSource, ITermDocLookup
    {
        public TextSegmentReader Segment { get; } = segment;

        public IReadOnlyList<string> FieldNames => Segment.FieldNames;

        public ITextFieldReader? GetTextField(string field) => Segment.GetTextField(field);

        // Delete-by-value reaches committed segments through the keyword field's postings.
        public void CollectDocs(string field, ReadOnlySpan<byte> term, List<int> destination)
        {
            if (Segment.GetTextField(field) is not { } reader || !reader.TryGetTerm(term, out TermInfo info))
            {
                return;
            }

            PostingsIterator postings = reader.GetPostings(info, PostingsFeatures.None);
            for (int d = postings.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = postings.NextDoc())
            {
                destination.Add(d);
            }
        }

        public void Dispose() => Segment.Dispose();
    }
}
