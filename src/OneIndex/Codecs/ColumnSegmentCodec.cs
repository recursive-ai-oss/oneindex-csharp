using System.Buffers.Binary;
using System.Text;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>
/// The column codec: owns the <c>columns</c> part, the per-document values of every field marked as a column or facet in the schema
/// (keyword, integer, floating-point, date, bool, geo point; single- or multi-valued; documents may lack a value). See
/// docs/columns.md for the format. A flush collects the buffered documents' values; a merge copies the live documents' values from
/// the sources through their document maps and builds the keyword dictionary again from the keywords still in use, so merging gives
/// the bytes that flushing the same documents would.
/// </summary>
public sealed class ColumnSegmentCodec : ISegmentCodec
{
    internal static readonly uint Magic = IndexFile.Magic("OIXK");

    /// <inheritdoc/>
    public string Name => "columns";

    /// <inheritdoc/>
    public IReadOnlyList<string> Parts { get; } = [SegmentParts.Columns];

    /// <inheritdoc/>
    public void Flush(SegmentFlushContext context, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        List<FieldDefinition> fields = ColumnFields(context.Schema);
        if (fields.Count == 0 || context.Documents.Count == 0)
        {
            return;
        }

        int maxDoc = context.Documents.Count;
        var distinct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (FieldDefinition f in fields.Where(f => f.Type == FieldType.Keyword))
        {
            distinct[f.Name] = new HashSet<string>(StringComparer.Ordinal);
        }

        if (distinct.Count > 0)
        {
            foreach (Document doc in context.Documents)
            {
                foreach (DocumentField v in doc.Fields)
                {
                    if (distinct.TryGetValue(v.Name, out HashSet<string>? set))
                    {
                        set.Add(v.Value.AsText());
                    }
                }
            }
        }

        var builders = new Dictionary<string, ColumnBuilder>(StringComparer.Ordinal);
        var ordinals = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (FieldDefinition f in fields)
        {
            byte[][]? dictionary = null;
            if (f.Type == FieldType.Keyword)
            {
                dictionary = ColumnValues.SortedTerms(distinct[f.Name]);
                var map = new Dictionary<string, int>(dictionary.Length, StringComparer.Ordinal);
                for (int i = 0; i < dictionary.Length; i++)
                {
                    map[Encoding.UTF8.GetString(dictionary[i])] = i;
                }

                ordinals[f.Name] = map;
            }

            builders[f.Name] = new ColumnBuilder(f.Name, f.Type, maxDoc, dictionary);
        }

        for (int d = 0; d < maxDoc; d++)
        {
            if ((d & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            foreach (DocumentField v in context.Documents[d].Fields)
            {
                if (!builders.TryGetValue(v.Name, out ColumnBuilder? b))
                {
                    continue;
                }

                switch (b.Type)
                {
                    case FieldType.Keyword:
                        b.Add(d, ordinals[v.Name][v.Value.AsText()]);
                        break;
                    case FieldType.GeoPoint:
                        (double lat, double lon) = v.Value.AsGeoPoint();
                        b.Add(d, SortableBits.Encode(lat), SortableBits.Encode(lon));
                        break;
                    default:
                        b.Add(d, ColumnValues.Raw(b.Type, v.Value));
                        break;
                }
            }
        }

        Write(maxDoc, builders.Values, sink, cancellationToken);
    }

    /// <inheritdoc/>
    public void Merge(SegmentMergeContext context, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        List<FieldDefinition> fields = ColumnFields(context.Schema);
        if (fields.Count == 0 || context.DocumentCount == 0)
        {
            return;
        }

        var builders = new List<ColumnBuilder>();
        foreach (FieldDefinition f in fields)
        {
            var columns = new SegmentColumn?[context.Sources.Count];
            for (int s = 0; s < columns.Length; s++)
            {
                if (context.Sources[s].Reader is ColumnPartReader part && part.GetColumn(f.Name) is SegmentColumn c)
                {
                    if (c.Type != f.Type)
                    {
                        throw new InvalidOperationException($"Column '{f.Name}' was written as {c.Type} but the schema declares {f.Type}.");
                    }

                    columns[s] = c;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            builders.Add(f.Type == FieldType.Keyword ? MergeKeywords(f, context, columns, cancellationToken) : MergeNumbers(f, context, columns, cancellationToken));
        }

        Write(context.DocumentCount, builders, sink, cancellationToken);
    }

    /// <inheritdoc/>
    public ISegmentPartReader? OpenReader(SegmentReaderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.DeferOptionalParts)
        {
            return context.Info.FindPart(SegmentParts.Columns) is null ? null : new DeferredColumnSource(new DeferredPart<ColumnPartReader>(() => OpenNow(context, cancellationToken)));
        }

        return OpenNow(context, cancellationToken);
    }

    private static ColumnPartReader? OpenNow(SegmentReaderContext context, CancellationToken cancellationToken)
    {
        IRangeReader? reader = context.OpenPart(SegmentParts.Columns, out SegmentFile? file, cancellationToken);
        if (reader is null)
        {
            return null;
        }

        byte[] bytes = ResidentFile.Read(reader, context.Limits, cancellationToken);
        return ColumnPartReader.Parse(file!.FileName, bytes, context.Info.DocCount, context.Limits);
    }

    private static List<FieldDefinition> ColumnFields(Schema schema)
    {
        var fields = schema.Fields.Where(ColumnValues.IsColumnField).ToList();
        fields.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return fields;
    }

    private static ColumnBuilder MergeNumbers(FieldDefinition f, SegmentMergeContext context, SegmentColumn?[] columns, CancellationToken cancellationToken)
    {
        var b = new ColumnBuilder(f.Name, f.Type, context.DocumentCount, null);
        for (int s = 0; s < columns.Length; s++)
        {
            SegmentColumn? c = columns[s];
            MergeSource source = context.Sources[s];
            if (c is null)
            {
                continue;
            }

            for (int d = 0; d < source.MaxDoc; d++)
            {
                int newDoc = source.DocMap[d];
                if (newDoc < 0)
                {
                    continue;
                }

                if ((d & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int n = c.ValueCount(d);
                for (int i = 0; i < n; i++)
                {
                    b.Add(newDoc, c.RawValue(d, i), f.Type == FieldType.GeoPoint ? c.RawSecond(d, i) : 0);
                }
            }
        }

        return b;
    }

    private static ColumnBuilder MergeKeywords(FieldDefinition f, SegmentMergeContext context, SegmentColumn?[] columns, CancellationToken cancellationToken)
    {
        // Pass 1: which keywords do the live documents still use?
        var used = new List<(int Source, int Ordinal)>();
        for (int s = 0; s < columns.Length; s++)
        {
            SegmentColumn? c = columns[s];
            if (c is null)
            {
                continue;
            }

            var seen = new bool[c.TermCount];
            MergeSource source = context.Sources[s];
            for (int d = 0; d < source.MaxDoc; d++)
            {
                if (source.DocMap[d] < 0)
                {
                    continue;
                }

                if ((d & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int n = c.ValueCount(d);
                for (int i = 0; i < n; i++)
                {
                    int o = c.GetOrdinal(d, i);
                    if (!seen[o])
                    {
                        seen[o] = true;
                        used.Add((s, o));
                    }
                }
            }
        }

        used.Sort((x, y) => columns[x.Source]!.GetTerm(x.Ordinal).SequenceCompareTo(columns[y.Source]!.GetTerm(y.Ordinal)));
        var dictionary = new List<byte[]>();
        var remap = new int[columns.Length][];
        for (int s = 0; s < columns.Length; s++)
        {
            remap[s] = columns[s] is { } c ? new int[c.TermCount] : [];
        }

        for (int i = 0; i < used.Count; i++)
        {
            ReadOnlySpan<byte> term = columns[used[i].Source]!.GetTerm(used[i].Ordinal);
            if (dictionary.Count == 0 || !term.SequenceEqual(dictionary[^1]))
            {
                dictionary.Add(term.ToArray());
            }

            remap[used[i].Source][used[i].Ordinal] = dictionary.Count - 1;
        }

        // Pass 2: the live documents' values, renumbered.
        var b = new ColumnBuilder(f.Name, f.Type, context.DocumentCount, [.. dictionary]);
        for (int s = 0; s < columns.Length; s++)
        {
            SegmentColumn? c = columns[s];
            MergeSource source = context.Sources[s];
            if (c is null)
            {
                continue;
            }

            for (int d = 0; d < source.MaxDoc; d++)
            {
                int newDoc = source.DocMap[d];
                if (newDoc < 0)
                {
                    continue;
                }

                int n = c.ValueCount(d);
                for (int i = 0; i < n; i++)
                {
                    b.Add(newDoc, remap[s][c.GetOrdinal(d, i)]);
                }
            }
        }

        return b;
    }

    private static void Write(int maxDoc, IEnumerable<ColumnBuilder> builders, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        var columns = builders.Where(b => b.ValueCount > 0).OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
        if (columns.Count == 0)
        {
            return;
        }

        using SegmentFileBuilder file = sink.Create(SegmentParts.Columns, "OIXK", CodecRegistry.NewestVersion(SegmentParts.Columns), cancellationToken);
        var head = new ByteBuilder(16);
        head.WriteUInt32((uint)maxDoc);
        file.Write(head.Span, cancellationToken);
        var directory = new ByteBuilder(256);
        directory.WriteVarUInt((uint)columns.Count);
        var section = new ByteBuilder(1 << 16);
        foreach (ColumnBuilder b in columns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ColumnLayout layout = b.ChooseLayout();
            (long min, long max) = b.Range();
            section.Clear();
            b.WriteSection(layout, section);
            long start = file.Position;
            file.Write(section.Span, cancellationToken);
            byte[] name = Encoding.UTF8.GetBytes(b.Name);
            directory.WriteVarUInt((uint)name.Length);
            directory.Write(name);
            directory.WriteByte((byte)b.Type);
            directory.WriteByte((byte)layout);
            directory.WriteVarUInt((uint)b.DocsWithValue);
            directory.WriteVarUInt((uint)b.ValueCount);
            directory.WriteUInt64((ulong)min);
            directory.WriteUInt64((ulong)max);
            directory.WriteVarUInt((ulong)start);
            directory.WriteVarUInt((ulong)section.Length);
        }

        long directoryAt = file.Position;
        file.Write(directory.Span, cancellationToken);
        var trailer = new ByteBuilder(8);
        trailer.WriteUInt64((ulong)directoryAt);
        file.Write(trailer.Span, cancellationToken);
        file.Complete(cancellationToken);
    }
}

/// <summary>A segment's columns, opened. Implements <see cref="IColumnSource"/>.</summary>
internal sealed class ColumnPartReader : ISegmentPartReader, IColumnSource
{
    private readonly Dictionary<string, SegmentColumn> _columns = new(StringComparer.Ordinal);

    public static ColumnPartReader Parse(string fileName, byte[] bytes, int maxDoc, IndexLimits limits)
    {
        IndexFile.Validate(fileName, bytes, ColumnSegmentCodec.Magic, CodecRegistry.NewestVersion(SegmentParts.Columns));
        if (bytes.Length < IndexFile.HeaderLength + 4 + 8 + IndexFile.FooterLength)
        {
            throw new CorruptIndexException(fileName, "too short to hold a column directory");
        }

        uint fileDocs = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(IndexFile.HeaderLength));
        if (fileDocs != (uint)maxDoc)
        {
            throw new CorruptIndexException(fileName, $"the columns cover {fileDocs} documents, the commit says {maxDoc}");
        }

        int trailerAt = bytes.Length - IndexFile.FooterLength - 8;
        ulong directoryAt = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(trailerAt));
        int sectionsStart = IndexFile.HeaderLength + 4;
        if (directoryAt < (ulong)sectionsStart || directoryAt >= (ulong)trailerAt)
        {
            throw new CorruptIndexException(fileName, "the column directory offset lies outside the file");
        }

        var cursor = new ByteCursor(fileName, bytes, (int)directoryAt, trailerAt);
        var reader = new ColumnPartReader();
        int count = ResidentFile.ReadCount(fileName, ref cursor, limits.MaxFields, "column count");
        string? previous = null;
        long expectedStart = sectionsStart;
        for (int i = 0; i < count; i++)
        {
            int nameLength = ResidentFile.ReadCount(fileName, ref cursor, limits.MaxStringBytes, "column name length");
            string name;
            try
            {
                name = new UTF8Encoding(false, true).GetString(cursor.ReadBytes(nameLength));
            }
            catch (DecoderFallbackException)
            {
                throw new CorruptIndexException(fileName, "a column name is not valid UTF-8");
            }

            if (previous is not null && string.CompareOrdinal(previous, name) >= 0)
            {
                throw new CorruptIndexException(fileName, "column names are not in strictly increasing order");
            }

            previous = name;
            var type = (FieldType)cursor.ReadByte();
            byte layoutByte = cursor.ReadByte();
            if (type is not (FieldType.Keyword or FieldType.Integral or FieldType.Fractional or FieldType.Date or FieldType.Bool or FieldType.GeoPoint) || layoutByte > (byte)ColumnLayout.Multi)
            {
                throw new CorruptIndexException(fileName, $"column '{name}' has an unknown type or layout");
            }

            ulong docs = cursor.ReadVarUInt();
            ulong values = cursor.ReadVarUInt();
            long min = cursor.ReadInt64();
            long max = cursor.ReadInt64();
            ulong start = cursor.ReadVarUInt();
            ulong length = cursor.ReadVarUInt();
            if (docs > (ulong)maxDoc || values > int.MaxValue || start != (ulong)expectedStart || length > directoryAt - start)
            {
                throw new CorruptIndexException(fileName, $"column '{name}' has an invalid directory entry");
            }

            expectedStart = (long)(start + length);
            reader._columns[name] = new SegmentColumn(fileName, bytes, maxDoc, type, (ColumnLayout)layoutByte, (long)docs, (long)values, min, max, (int)start, (int)(start + length));
        }

        if (expectedStart != (long)directoryAt || cursor.Remaining != 0)
        {
            throw new CorruptIndexException(fileName, "the column sections and directory do not tile the file");
        }

        return reader;
    }

    public IEnumerable<string> Names => _columns.Keys;

    public IColumnReader? GetColumn(string field) => _columns.GetValueOrDefault(field);

    public void Dispose()
    {
    }
}

/// <summary>Column access that opens the columns file when a query first asks for a column (static indexes).</summary>
internal sealed class DeferredColumnSource(DeferredPart<ColumnPartReader> part) : ISegmentPartReader, IColumnSource
{
    public IColumnReader? GetColumn(string field) => part.Value?.GetColumn(field);

    public void Dispose() => part.Dispose();
}
