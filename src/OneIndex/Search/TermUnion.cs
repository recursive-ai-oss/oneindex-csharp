using System.Numerics;

namespace OneIndex.Search;

/// <summary>The documents that contain any term of an enumeration, as one set per segment.</summary>
internal static class TermUnion
{
    /// <summary>
    /// Opens the postings of every term the enumeration yields and merges them lazily, one window of documents at a time: a search
    /// that stops after the first few hits (a top-k of equal scores) reads only the first window of each term. There is no per-term
    /// scorer, weight or statistic.
    /// </summary>
    /// <param name="leaf">The segment.</param>
    /// <param name="field">The field whose terms are enumerated.</param>
    /// <param name="open">Opens the enumeration of matching terms on the segment's field.</param>
    /// <returns>The iterator, or <see langword="null"/> when the segment has no such field or no term matches.</returns>
    public static DocIdSetIterator? Documents(LeafContext leaf, string field, Func<ITextFieldReader, ITermEnumerator> open)
    {
        if (leaf.Reader.GetTextField(field) is not { } reader)
        {
            return null;
        }

        // Rare terms are decoded straight into a bit set with one reused iterator; each long list keeps its own iterator and is read
        // window by window, so that a top-k that stops early does not decode all of them.
        var longLists = new List<PostingsIterator>();
        ulong[]? small = null;
        PostingsIterator? reused = null;
        long cost = 0;
        using (ITermEnumerator terms = open(reader))
        {
            while (terms.MoveNext())
            {
                TermInfo info = terms.Info;
                cost += info.DocFreq;
                if (info.DocFreq > EagerDocFreq)
                {
                    longLists.Add(reader.GetPostings(info, PostingsFeatures.None));
                    continue;
                }

                small ??= new ulong[(leaf.Reader.MaxDoc + 63) >> 6];
                reused = reader.GetPostings(info, PostingsFeatures.None, reused);
                int doc;
                while ((doc = reused.NextDoc()) != DocIdSetIterator.NoMoreDocs)
                {
                    small[doc >> 6] |= 1UL << (doc & 63);
                }
            }
        }

        return small is null && longLists.Count == 0 ? null : new TermUnionIterator([.. longLists], small, Math.Min(cost, leaf.Reader.MaxDoc));
    }

    private const int EagerDocFreq = 512;
}

/// <summary>
/// The union of several postings lists. Documents are merged through a bit window of <see cref="WindowBits"/> documents that moves
/// forward; each list is read only as far as the window needs.
/// </summary>
internal sealed class TermUnionIterator : DocIdSetIterator
{
    private const int WindowBits = 4096;
    private const int WindowWords = WindowBits / 64;
    private readonly PostingsIterator[] _lists;
    private readonly ulong[]? _small;
    private readonly int[] _current;
    private readonly long _cost;
    private readonly ulong[] _bits = new ulong[WindowWords];
    private int _live;
    private int _base = -WindowBits;
    private int _doc = -1;

    public TermUnionIterator(PostingsIterator[] lists, ulong[]? small, long cost)
    {
        _lists = lists;
        _small = small;
        _cost = cost;
        _live = lists.Length;
        _current = new int[lists.Length];
        for (int i = 0; i < lists.Length; i++)
        {
            _current[i] = lists[i].NextDoc();
        }

        Compact();
    }

    public override int DocId => _doc;

    public override long Cost => _cost;

    public override int NextDoc() => Advance(_doc + 1);

    public override int Advance(int target)
    {
        if (_doc == NoMoreDocs)
        {
            return NoMoreDocs;
        }

        if (target >= _base + WindowBits)
        {
            if (!Fill(target & ~(WindowBits - 1)))
            {
                return _doc = NoMoreDocs;
            }
        }

        while (true)
        {
            int from = Math.Max(target - _base, 0);
            int w = from >> 6;
            ulong bits = _bits[w] & (ulong.MaxValue << (from & 63));
            while (true)
            {
                if (bits != 0)
                {
                    return _doc = _base + (w << 6) + BitOperations.TrailingZeroCount(bits);
                }

                if (++w >= WindowWords)
                {
                    break;
                }

                bits = _bits[w];
            }

            if (!Fill(_base + WindowBits))
            {
                return _doc = NoMoreDocs;
            }

            target = _base;
        }
    }

    // Loads the window starting at windowStart; when nothing is in it, jumps to the window of the smallest next document.
    private bool Fill(int windowStart)
    {
        while (_live > 0 || (_small is not null && (windowStart >> 6) < _small.Length))
        {
            Array.Clear(_bits);
            int end = windowStart + WindowBits;
            bool any = false;
            int min = NoMoreDocs;
            if (_small is not null)
            {
                int first = windowStart >> 6;
                int last = Math.Min(first + WindowWords, _small.Length);
                for (int w = first; w < last; w++)
                {
                    ulong word = _small[w];
                    _bits[w - first] = word;
                    any |= word != 0;
                }

                if (!any)
                {
                    for (int w = last; w < _small.Length; w++)
                    {
                        if (_small[w] != 0)
                        {
                            min = (w << 6) + BitOperations.TrailingZeroCount(_small[w]);
                            break;
                        }
                    }
                }
            }

            for (int i = 0; i < _live; i++)
            {
                int d = _current[i];
                if (d < windowStart)
                {
                    d = _lists[i].Advance(windowStart);
                }

                while (d < end)
                {
                    int rel = d - windowStart;
                    _bits[rel >> 6] |= 1UL << (rel & 63);
                    any = true;
                    d = _lists[i].NextDoc();
                }

                _current[i] = d;
                if (d < min)
                {
                    min = d;
                }
            }

            _base = windowStart;
            Compact();
            if (any)
            {
                return true;
            }

            if (min == NoMoreDocs)
            {
                return false;
            }

            windowStart = min & ~(WindowBits - 1);
        }

        _base = windowStart;
        return false;
    }

    // Drops finished lists so later windows do not visit them.
    private void Compact()
    {
        for (int i = 0; i < _live;)
        {
            if (_current[i] == NoMoreDocs)
            {
                _live--;
                _lists[i] = _lists[_live];
                _current[i] = _current[_live];
            }
            else
            {
                i++;
            }
        }
    }
}
