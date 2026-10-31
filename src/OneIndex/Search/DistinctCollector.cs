using OneIndex.Codecs;
using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>The best document of each of the best groups of a search.</summary>
/// <param name="TotalHits">How many documents matched, grouped or not.</param>
/// <param name="TotalGroups">How many groups the matches fall in: distinct values of the field, plus one for every match without a value.</param>
/// <param name="Hits">The best document of each of the best groups, best first: higher score first, equal scores by lower document id.</param>
public sealed record DistinctDocs(long TotalHits, long TotalGroups, IReadOnlyList<ScoreDoc> Hits);

/// <summary>
/// Collapses matches by the value of a column (Meilisearch's <c>distinctAttribute</c>): of all documents with the same value only the
/// best-scoring one is kept, and groups are ranked by their best document. A document with several values belongs to the group of its
/// first value; a document without a value forms a group of its own. Every match is visited (the groups' best scores are only known
/// at the end), so counts are exact; memory is proportional to the number of groups. Ties are broken by the lower global document id,
/// so a page is a pure function of the snapshot and <c>Skip</c> and <c>Take</c> stay consistent from page to page.
/// </summary>
public sealed class DistinctCollector : Collector
{
    private readonly string _field;
    private readonly int _count;
    private readonly Dictionary<string, int> _byKeyword = new(StringComparer.Ordinal);
    private readonly Dictionary<long, int> _byRaw = [];
    private float[] _scores = new float[16];
    private int[] _docs = new int[16];
    private int _groups;
    private long _totalHits;

    /// <summary>Creates the collector.</summary>
    /// <param name="field">The column to collapse by: keyword, bool, integral, fractional or date.</param>
    /// <param name="count">How many groups to return, at least 1 (<c>Skip + Take</c>).</param>
    public DistinctCollector(string field, int count)
    {
        ArgumentException.ThrowIfNullOrEmpty(field);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        _field = field;
        _count = count;
    }

    /// <inheritdoc/>
    public override ScoreMode ScoreMode => ScoreMode.Complete;

    /// <inheritdoc/>
    public override LeafCollector GetLeafCollector(LeafContext leaf)
    {
        IColumnReader? col = leaf.Reader.GetColumn(_field);
        if (col is not null && col.Type == FieldType.GeoPoint)
        {
            throw new ArgumentException($"Cannot collapse by '{_field}': geo points have no single value to group by.");
        }

        return new Leaf(this, col, leaf.DocBase);
    }

    /// <summary>The best groups so far.</summary>
    /// <returns>The result; <see cref="DistinctDocs.TotalGroups"/> is exact.</returns>
    public DistinctDocs GetTopDocs()
    {
        var order = new int[_groups];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        float[] scores = _scores;
        int[] docs = _docs;
        Array.Sort(order, (a, b) =>
        {
            int c = scores[b].CompareTo(scores[a]);
            return c != 0 ? c : docs[a].CompareTo(docs[b]);
        });
        int take = Math.Min(_count, order.Length);
        var hits = new ScoreDoc[take];
        for (int i = 0; i < take; i++)
        {
            hits[i] = new ScoreDoc(docs[order[i]], scores[order[i]]);
        }

        return new DistinctDocs(_totalHits, _groups, hits);
    }

    private int NewGroup(float score, int doc)
    {
        if (_groups == _scores.Length)
        {
            Array.Resize(ref _scores, _groups * 2);
            Array.Resize(ref _docs, _groups * 2);
        }

        _scores[_groups] = score;
        _docs[_groups] = doc;
        return _groups++;
    }

    private void Improve(int group, float score, int doc)
    {
        // Documents arrive in increasing global id, so on a tie the earlier document stays.
        if (score > _scores[group])
        {
            _scores[group] = score;
            _docs[group] = doc;
        }
    }

    private sealed class Leaf : LeafCollector
    {
        private readonly DistinctCollector _owner;
        private readonly IColumnReader? _col;
        private readonly SegmentColumn? _keywords;
        private readonly int[]? _ordinalGroup;
        private readonly int _docBase;
        private Scorer? _scorer;

        public Leaf(DistinctCollector owner, IColumnReader? col, int docBase)
        {
            _owner = owner;
            _col = col;
            _docBase = docBase;
            if (col is { Type: FieldType.Keyword })
            {
                _keywords = col as SegmentColumn ?? throw new NotSupportedException("Collapsing by a keyword needs the segment column reader.");
                _ordinalGroup = new int[_keywords.TermCount];
                Array.Fill(_ordinalGroup, -1);
            }
        }

        public override void SetScorer(Scorer scorer) => _scorer = scorer;

        public override void Collect(int docId)
        {
            float score = _scorer!.Score();
            if (float.IsNaN(score))
            {
                score = float.NegativeInfinity;
            }

            int doc = _docBase + docId;
            _owner._totalHits++;
            if (_col is null || _col.ValueCount(docId) == 0)
            {
                _owner.NewGroup(score, doc);
                return;
            }

            if (_keywords is not null)
            {
                int ord = _keywords.GetOrdinal(docId);
                int group = _ordinalGroup![ord];
                if (group < 0)
                {
                    string key = System.Text.Encoding.UTF8.GetString(_keywords.GetTerm(ord));
                    if (!_owner._byKeyword.TryGetValue(key, out group))
                    {
                        group = _owner.NewGroup(score, doc);
                        _owner._byKeyword.Add(key, group);
                        _ordinalGroup[ord] = group;
                        return;
                    }

                    _ordinalGroup[ord] = group;
                }

                _owner.Improve(group, score, doc);
                return;
            }

            long raw;
            if (_col.Type == FieldType.Fractional)
            {
                double d = _col.GetFloat64(docId);
                raw = SortableBits.Encode(d == 0 ? 0.0 : d); // -0 and +0 are one value
            }
            else
            {
                raw = _col.GetInt64(docId);
            }

            if (_owner._byRaw.TryGetValue(raw, out int g))
            {
                _owner.Improve(g, score, doc);
            }
            else
            {
                _owner._byRaw.Add(raw, _owner.NewGroup(score, doc));
            }
        }
    }
}
