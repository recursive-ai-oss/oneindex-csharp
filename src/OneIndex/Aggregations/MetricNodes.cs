using OneIndex.Codecs;
using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Aggregations;

internal sealed class StatsNode(Aggregation spec, bool isFloat) : AggNode
{
    private long _count;
    private long _minI = long.MaxValue;
    private long _maxI = long.MinValue;
    private double _minD = double.PositiveInfinity;
    private double _maxD = double.NegativeInfinity;
    private Int128 _sumI;
    private double _sumD;
    private double _compensation;

    public override AggLeaf StartLeaf(LeafContext leaf)
    {
        IColumnReader? col = leaf.Reader.GetColumn(spec.Field);
        return col is null ? NoOpLeaf.Instance : new Leaf(this, col);
    }

    public override AggregationResult Build()
    {
        if (_count == 0)
        {
            return new StatsResult(spec.Name, 0, null, null, 0);
        }

        return isFloat
            ? new StatsResult(spec.Name, _count, _minD, _maxD, _sumD + _compensation)
            : new StatsResult(spec.Name, _count, _minI, _maxI, (double)_sumI);
    }

    private void AddInt(long v)
    {
        _count++;
        if (v < _minI)
        {
            _minI = v;
        }

        if (v > _maxI)
        {
            _maxI = v;
        }

        _sumI += v;
    }

    private void AddFloat(double v)
    {
        _count++;
        if (v < _minD)
        {
            _minD = v;
        }

        if (v > _maxD)
        {
            _maxD = v;
        }

        // Neumaier's compensated summation: the rounding error of each addition is kept and added back at the end.
        double t = _sumD + v;
        _compensation += Math.Abs(_sumD) >= Math.Abs(v) ? (_sumD - t) + v : (v - t) + _sumD;
        _sumD = t;
    }

    private sealed class Leaf(StatsNode node, IColumnReader col) : AggLeaf
    {
        public override void Collect(int doc)
        {
            int n = col.ValueCount(doc);
            for (int i = 0; i < n; i++)
            {
                if (node.IsFloat)
                {
                    double v = col.GetFloat64(doc, i);
                    if (!double.IsNaN(v))
                    {
                        node.AddFloat(v);
                    }
                }
                else
                {
                    node.AddInt(col.GetInt64(doc, i));
                }
            }
        }
    }

    private bool IsFloat => isFloat;
}

internal sealed class CardinalityNode(CardinalityAggregation spec, FieldType type) : AggNode
{
    private readonly HyperLogLogSketch _sketch = new(spec.Precision);

    public override AggLeaf StartLeaf(LeafContext leaf)
    {
        IColumnReader? col = leaf.Reader.GetColumn(spec.Field);
        if (col is null)
        {
            return NoOpLeaf.Instance;
        }

        return type == FieldType.Keyword
            ? new KeywordLeaf(_sketch, col as SegmentColumn ?? throw new NotSupportedException("Keyword aggregations need the segment column reader."))
            : new NumericLeaf(_sketch, col, type == FieldType.Fractional);
    }

    public override AggregationResult Build() => new CardinalityResult(spec.Name, _sketch.Estimate());

    private sealed class KeywordLeaf(HyperLogLogSketch sketch, SegmentColumn col) : AggLeaf
    {
        // Ordinals are local to the segment: remember which were seen, and hash each distinct keyword once at the end.
        private readonly bool[] _seen = new bool[col.TermCount];
        private bool _finished;

        public override void Collect(int doc)
        {
            int n = col.ValueCount(doc);
            for (int i = 0; i < n; i++)
            {
                _seen[col.GetOrdinal(doc, i)] = true;
            }
        }

        public override void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            for (int ord = 0; ord < _seen.Length; ord++)
            {
                if (_seen[ord])
                {
                    sketch.AddHash(HyperLogLogSketch.Hash(col.GetTerm(ord)));
                }
            }
        }
    }

    private sealed class NumericLeaf(HyperLogLogSketch sketch, IColumnReader col, bool isFloat) : AggLeaf
    {
        public override void Collect(int doc)
        {
            int n = col.ValueCount(doc);
            for (int i = 0; i < n; i++)
            {
                long raw;
                if (isFloat)
                {
                    double d = col.GetFloat64(doc, i);
                    raw = SortableBits.Encode(d == 0 ? 0.0 : d);
                }
                else
                {
                    raw = col.GetInt64(doc, i);
                }

                sketch.AddHash(HyperLogLogSketch.Hash(raw));
            }
        }
    }
}

internal sealed class PercentilesNode(PercentilesAggregation spec, bool isFloat) : AggNode
{
    private readonly TDigest _digest = new(spec.Compression);

    public override AggLeaf StartLeaf(LeafContext leaf)
    {
        IColumnReader? col = leaf.Reader.GetColumn(spec.Field);
        return col is null ? NoOpLeaf.Instance : new Leaf(_digest, col, isFloat);
    }

    public override AggregationResult Build()
    {
        var values = new List<KeyValuePair<double, double>>(spec.Percents.Count);
        foreach (double p in spec.Percents)
        {
            values.Add(new KeyValuePair<double, double>(p, _digest.Quantile(p / 100.0)));
        }

        return new PercentilesResult(spec.Name, values);
    }

    private sealed class Leaf(TDigest digest, IColumnReader col, bool isFloat) : AggLeaf
    {
        public override void Collect(int doc)
        {
            int n = col.ValueCount(doc);
            for (int i = 0; i < n; i++)
            {
                digest.Add(isFloat ? col.GetFloat64(doc, i) : col.GetInt64(doc, i));
            }
        }
    }
}
