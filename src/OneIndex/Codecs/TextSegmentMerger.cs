using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>One segment taking part in a merge.</summary>
/// <param name="Reader">The segment's text index.</param>
/// <param name="DocMap">For each of the segment's documents (<see cref="TextSegmentReader.MaxDoc"/> entries), its document id in the merged segment, or -1 for a document that is dropped (deleted). Ids must increase with the old id, and every id of a later source must exceed every id of an earlier one.</param>
public sealed record TextMergeSource(TextSegmentReader Reader, int[] DocMap);

/// <summary>
/// Merges the text indexes of segments into one through a <see cref="TextSegmentWriter"/>: per field a k-way merge of the sources'
/// term dictionaries in byte order, with each term's postings copied through the document map and deleted documents left out. A
/// term whose documents were all deleted disappears. Because the written format is canonical, the merged files are
/// byte-identical to files built directly from the surviving documents.
/// </summary>
public static class TextSegmentMerger
{
    private const int CancellationInterval = 256;

    /// <summary>Merges the sources, writing every field to <paramref name="writer"/>; the caller then calls <see cref="TextSegmentWriter.Finish"/>.</summary>
    /// <param name="writer">The merged segment's writer; its <see cref="TextSegmentWriter.MaxDoc"/> must exceed every mapped id.</param>
    /// <param name="sources">The segments to merge, in document order.</param>
    /// <param name="cancellationToken">Cancels the merge; checked every few hundred terms.</param>
    /// <exception cref="InvalidOperationException">A field's features differ between sources, or a document map is not increasing.</exception>
    /// <exception cref="CorruptIndexException">A source is damaged.</exception>
    public static void Merge(TextSegmentWriter writer, IReadOnlyList<TextMergeSource> sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(sources);
        foreach (TextMergeSource s in sources)
        {
            if (s.DocMap.Length != s.Reader.MaxDoc)
            {
                throw new ArgumentException("A document map must have one entry per source document.", nameof(sources));
            }
        }

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (TextMergeSource s in sources)
        {
            foreach (string n in s.Reader.FieldNames)
            {
                names.Add(n);
            }
        }

        foreach (string name in names)
        {
            MergeField(writer, sources, name, cancellationToken);
        }
    }

    private static void MergeField(TextSegmentWriter writer, IReadOnlyList<TextMergeSource> sources, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var readers = new List<(ITextFieldReader Field, int[] Map)>();
        PostingsFeatures? features = null;
        foreach (TextMergeSource s in sources)
        {
            ITextFieldReader? f = s.Reader.GetTextField(name);
            if (f is null)
            {
                continue;
            }

            if (features is not null && features != f.IndexedFeatures)
            {
                throw new InvalidOperationException($"Field '{name}' is indexed with different features in the segments being merged.");
            }

            features = f.IndexedFeatures;
            readers.Add((f, s.DocMap));
        }

        bool positions = (features!.Value & PostingsFeatures.Positions) == PostingsFeatures.Positions;
        bool offsets = (features.Value & PostingsFeatures.Offsets) == PostingsFeatures.Offsets;
        var lengths = new int[writer.MaxDoc];
        foreach (TextMergeSource s in sources)
        {
            ITextFieldReader? f = s.Reader.GetTextField(name);
            if (f is null)
            {
                continue;
            }

            for (int d = 0; d < s.DocMap.Length; d++)
            {
                int target = s.DocMap[d];
                if (target >= 0)
                {
                    lengths[target] = f.GetFieldLength(d);
                }
            }
        }

        writer.BeginField(name, features.Value, lengths);
        var enumerators = new ITermEnumerator[readers.Count];
        var live = new bool[readers.Count];
        var postings = new TermPostings?[readers.Count];
        try
        {
            for (int i = 0; i < readers.Count; i++)
            {
                enumerators[i] = readers[i].Field.GetTerms(default);
                live[i] = enumerators[i].MoveNext();
            }

            byte[] current = new byte[64];
            int termsDone = 0;
            while (true)
            {
                int min = -1;
                for (int i = 0; i < live.Length; i++)
                {
                    if (live[i] && (min < 0 || enumerators[i].Term.SequenceCompareTo(enumerators[min].Term) < 0))
                    {
                        min = i;
                    }
                }

                if (min < 0)
                {
                    break;
                }

                if ((++termsDone % CancellationInterval) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                ReadOnlySpan<byte> minTerm = enumerators[min].Term;
                if (current.Length < minTerm.Length)
                {
                    current = new byte[minTerm.Length * 2];
                }

                minTerm.CopyTo(current);
                int currentLength = minTerm.Length;
                writer.BeginTerm(current.AsSpan(0, currentLength));
                for (int i = 0; i < live.Length; i++)
                {
                    if (!live[i] || !enumerators[i].Term.SequenceEqual(current.AsSpan(0, currentLength)))
                    {
                        continue;
                    }

                    CopyPostings(writer, readers[i].Field, readers[i].Map, enumerators[i].Info, ref postings[i], positions, offsets);
                    live[i] = enumerators[i].MoveNext();
                }

                writer.EndTerm();
            }
        }
        finally
        {
            foreach (ITermEnumerator? e in enumerators)
            {
                e?.Dispose();
            }
        }

        writer.EndField();
    }

    private static void CopyPostings(TextSegmentWriter writer, ITextFieldReader field, int[] map, in TermInfo info, ref TermPostings? cached, bool positions, bool offsets)
    {
        PostingsFeatures want = offsets ? PostingsFeatures.Offsets : positions ? PostingsFeatures.Positions : PostingsFeatures.Frequencies;
        if (cached is null)
        {
            cached = (TermPostings)field.GetPostings(info, want);
        }
        else
        {
            cached.Reset(info, want);
        }

        TermPostings p = cached;
        int previous = -1;
        for (int doc = p.NextDoc(); doc != DocIdSetIterator.NoMoreDocs; doc = p.NextDoc())
        {
            int target = map[doc];
            if (target < 0)
            {
                continue;
            }

            if (target <= previous)
            {
                throw new InvalidOperationException("A document map must increase with the source's document ids.");
            }

            previous = target;
            int freq = p.Frequency;
            writer.AddDocument(target, freq);
            if (positions)
            {
                for (int k = 0; k < freq; k++)
                {
                    int position = p.NextPosition();
                    writer.AddPosition(position, offsets ? p.StartOffset : 0, offsets ? p.EndOffset : 0);
                }
            }
        }
    }
}
