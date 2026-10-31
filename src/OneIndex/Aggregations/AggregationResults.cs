using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace OneIndex.Aggregations;

/// <summary>
/// The value that identifies a bucket. <see cref="Text"/> is always set (a keyword, <c>true</c>/<c>false</c>, an invariant number, an ISO 8601
/// UTC time for dates, a range label); <see cref="Number"/> is the numeric value (NaN for keywords and ranges); <see cref="Whole"/> is the integer
/// value of integral, bool and date keys, and the start in milliseconds of date-histogram buckets (0 otherwise).
/// </summary>
/// <param name="Text">The key as text.</param>
/// <param name="Whole">The integer form.</param>
/// <param name="Number">The numeric form.</param>
public readonly record struct BucketKey(string Text, long Whole, double Number)
{
    /// <inheritdoc/>
    public override string ToString() => Text;
}

/// <summary>The result of one <see cref="Aggregation"/>.</summary>
public abstract class AggregationResult
{
    private protected AggregationResult(string name) => Name = name;

    /// <summary>The name of the request.</summary>
    public string Name { get; }
}

/// <summary>One bucket of a bucket aggregation.</summary>
public sealed class Bucket
{
    internal Bucket(BucketKey key, long docCount, AggregationResultSet aggregations, double? from = null, double? to = null)
    {
        Key = key;
        DocCount = docCount;
        Aggregations = aggregations;
        From = from;
        To = to;
    }

    /// <summary>The bucket's key.</summary>
    public BucketKey Key { get; }

    /// <summary>How many matching documents fall in the bucket.</summary>
    public long DocCount { get; }

    /// <summary>The results of the sub-aggregations, over this bucket's documents.</summary>
    public AggregationResultSet Aggregations { get; }

    /// <summary>The inclusive lower bound of a range bucket; <see langword="null"/> for other buckets or an open end.</summary>
    public double? From { get; }

    /// <summary>The exclusive upper bound of a range bucket; <see langword="null"/> for other buckets or an open end.</summary>
    public double? To { get; }
}

/// <summary>The buckets of a terms, range, histogram or date histogram aggregation.</summary>
public sealed class BucketsResult : AggregationResult
{
    internal BucketsResult(string name, IReadOnlyList<Bucket> buckets, long otherDocCount)
        : base(name)
    {
        Buckets = buckets;
        OtherDocCount = otherDocCount;
    }

    /// <summary>The buckets, in the aggregation's order.</summary>
    public IReadOnlyList<Bucket> Buckets { get; }

    /// <summary>For terms: the sum of the document counts of the buckets cut off by <see cref="TermsAggregation.Size"/>. 0 for the others.</summary>
    public long OtherDocCount { get; }
}

/// <summary>The result of <see cref="StatsAggregation"/>.</summary>
public sealed class StatsResult : AggregationResult
{
    internal StatsResult(string name, long count, double? min, double? max, double sum)
        : base(name)
    {
        Count = count;
        Min = min;
        Max = max;
        Sum = sum;
    }

    /// <summary>The number of values.</summary>
    public long Count { get; }

    /// <summary>The smallest value, or <see langword="null"/> when there are none.</summary>
    public double? Min { get; }

    /// <summary>The largest value, or <see langword="null"/> when there are none.</summary>
    public double? Max { get; }

    /// <summary>The sum; exact for integral and date fields (rounded once to a double), compensated for fractional ones. 0 when there are no values.</summary>
    public double Sum { get; }

    /// <summary>The mean, or <see langword="null"/> when there are no values.</summary>
    public double? Avg => Count == 0 ? null : Sum / Count;
}

/// <summary>The result of <see cref="CardinalityAggregation"/>.</summary>
public sealed class CardinalityResult : AggregationResult
{
    internal CardinalityResult(string name, long value)
        : base(name)
    {
        Value = value;
    }

    /// <summary>The estimated number of distinct values.</summary>
    public long Value { get; }
}

/// <summary>The result of <see cref="PercentilesAggregation"/>.</summary>
public sealed class PercentilesResult : AggregationResult
{
    internal PercentilesResult(string name, IReadOnlyList<KeyValuePair<double, double>> values)
        : base(name)
    {
        Values = values;
    }

    /// <summary>Each requested percent (0 to 100) with its estimated value; the value is NaN when there are no values.</summary>
    public IReadOnlyList<KeyValuePair<double, double>> Values { get; }
}

/// <summary>Results addressed by request name; keeps the order of the requests.</summary>
public sealed class AggregationResultSet : IReadOnlyList<AggregationResult>
{
    internal static readonly AggregationResultSet Empty = new([]);

    private readonly AggregationResult[] _results;

    internal AggregationResultSet(AggregationResult[] results) => _results = results;

    /// <inheritdoc/>
    public int Count => _results.Length;

    /// <inheritdoc/>
    public AggregationResult this[int index] => _results[index];

    /// <summary>Gets a result by the name of its request.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The result.</returns>
    /// <exception cref="KeyNotFoundException">No request has this name.</exception>
    public AggregationResult this[string name]
        => TryGet(name, out AggregationResult? r) ? r : throw new KeyNotFoundException($"No aggregation is named '{name}'.");

    /// <summary>Gets a result by name.</summary>
    /// <param name="name">The name.</param>
    /// <param name="result">The result when found.</param>
    /// <returns><see langword="true"/> when found.</returns>
    public bool TryGet(string name, [NotNullWhen(true)] out AggregationResult? result)
    {
        foreach (AggregationResult r in _results)
        {
            if (string.Equals(r.Name, name, StringComparison.Ordinal))
            {
                result = r;
                return true;
            }
        }

        result = null;
        return false;
    }

    /// <summary>Gets a result by name as its concrete type.</summary>
    /// <typeparam name="TResult">The result type, for example <see cref="BucketsResult"/>.</typeparam>
    /// <param name="name">The name.</param>
    /// <returns>The result.</returns>
    /// <exception cref="KeyNotFoundException">No request has this name.</exception>
    /// <exception cref="InvalidCastException">The result is of another type.</exception>
    public TResult Get<TResult>(string name)
        where TResult : AggregationResult
        => this[name] as TResult ?? throw new InvalidCastException($"Aggregation '{name}' is a {this[name].GetType().Name}, not a {typeof(TResult).Name}.");

    /// <inheritdoc/>
    public IEnumerator<AggregationResult> GetEnumerator() => ((IEnumerable<AggregationResult>)_results).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Raised when an aggregation would produce more buckets than <see cref="AggregationCollector.MaxBuckets"/> (an interval that is too small for the data).</summary>
public sealed class TooManyBucketsException : InvalidOperationException
{
    internal TooManyBucketsException(string aggregation, long buckets)
        : base($"Aggregation '{aggregation}' would produce {buckets} buckets; the limit is {AggregationCollector.MaxBuckets}. Use a larger interval.")
    {
    }
}
