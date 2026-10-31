namespace OneIndex.Aggregations;

/// <summary>
/// A request for one aggregation over the documents a query matches. Requests are plain data: a name, a field and a few settings, so
/// they can be built in code or read from a description. Bucket aggregations may carry <see cref="BucketAggregation.SubAggregations"/>,
/// which run over the documents of each bucket. Names must be unique among siblings. The field must keep a column (see docs/columns.md).
/// </summary>
public abstract class Aggregation
{
    private protected Aggregation(string name, string field)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(field);
        Name = name;
        Field = field;
    }

    /// <summary>The name the result carries.</summary>
    public string Name { get; }

    /// <summary>The schema field the values come from.</summary>
    public string Field { get; }
}

/// <summary>An aggregation that groups documents into buckets and may aggregate inside each bucket.</summary>
public abstract class BucketAggregation : Aggregation
{
    private protected BucketAggregation(string name, string field)
        : base(name, field)
    {
    }

    /// <summary>Aggregations computed over the documents of every bucket. Default none.</summary>
    public IReadOnlyList<Aggregation> SubAggregations { get; init; } = [];
}

/// <summary>How <see cref="TermsAggregation"/> orders its buckets.</summary>
public enum TermsOrder : byte
{
    /// <summary>Most documents first; equal counts by key ascending.</summary>
    CountDescending,

    /// <summary>Key ascending: keywords in ordinal (UTF-16 code unit) order, numbers and dates by value.</summary>
    KeyAscending,

    /// <summary>Key descending.</summary>
    KeyDescending,
}

/// <summary>
/// One bucket per distinct value of a keyword, bool, integral, fractional or date field. A document with several values counts once in
/// the bucket of each distinct value (twice-listed values count once). Counts are exact.
/// </summary>
public sealed class TermsAggregation : BucketAggregation
{
    /// <summary>Creates the request.</summary>
    /// <param name="name">The result name.</param>
    /// <param name="field">The field.</param>
    public TermsAggregation(string name, string field)
        : base(name, field)
    {
    }

    /// <summary>How many buckets to return, at least 1. Default 10.</summary>
    public int Size { get; init; } = 10;

    /// <summary>The order; the first <see cref="Size"/> buckets in this order are returned. Default <see cref="TermsOrder.CountDescending"/>.</summary>
    public TermsOrder Order { get; init; } = TermsOrder.CountDescending;

    /// <summary>Buckets with fewer documents are dropped. Default 1; at least 1.</summary>
    public long MinDocCount { get; init; } = 1;
}

/// <summary>A numeric range: <c>From</c> inclusive, <c>To</c> exclusive; a missing bound is open.</summary>
/// <param name="From">The inclusive lower bound, or <see langword="null"/>.</param>
/// <param name="To">The exclusive upper bound, or <see langword="null"/>.</param>
/// <param name="Key">The bucket label; <c>"from-to"</c> with <c>*</c> for an open end when <see langword="null"/>.</param>
public sealed record AggregationRange(double? From, double? To, string? Key = null);

/// <summary>
/// One bucket per requested range over an integral, fractional or date field (dates compare as milliseconds). Ranges may overlap; a
/// document is counted once per range that holds at least one of its values. Values are compared as <see cref="double"/>, exact up
/// to 2^53. NaN is in no range. Buckets come in the order requested.
/// </summary>
public sealed class RangeAggregation : BucketAggregation
{
    /// <summary>Creates the request.</summary>
    /// <param name="name">The result name.</param>
    /// <param name="field">The field.</param>
    /// <param name="ranges">The ranges, at least one.</param>
    public RangeAggregation(string name, string field, IReadOnlyList<AggregationRange> ranges)
        : base(name, field)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        Ranges = ranges;
    }

    /// <summary>The ranges.</summary>
    public IReadOnlyList<AggregationRange> Ranges { get; }
}

/// <summary>
/// Fixed-width numeric buckets: a value <c>v</c> goes to the bucket whose key is <c>floor((v - Offset) / Interval) * Interval + Offset</c>.
/// Values that are NaN, infinite or whose bucket index does not fit 63 bits are ignored.
/// </summary>
public sealed class HistogramAggregation : BucketAggregation
{
    /// <summary>Creates the request.</summary>
    /// <param name="name">The result name.</param>
    /// <param name="field">An integral or fractional field.</param>
    /// <param name="interval">The bucket width, greater than 0 and finite.</param>
    public HistogramAggregation(string name, string field, double interval)
        : base(name, field)
    {
        Interval = interval;
    }

    /// <summary>The bucket width.</summary>
    public double Interval { get; }

    /// <summary>Shifts bucket boundaries. Default 0.</summary>
    public double Offset { get; init; }

    /// <summary>
    /// Buckets with fewer documents are dropped. With the default 0 the empty buckets between the first and last bucket that holds a
    /// document are returned too; use 1 to leave them out.
    /// </summary>
    public long MinDocCount { get; init; }
}

/// <summary>A calendar unit of <see cref="DateHistogramAggregation"/>. All are in UTC.</summary>
public enum DateInterval : byte
{
    /// <summary>One second.</summary>
    Second,

    /// <summary>One minute.</summary>
    Minute,

    /// <summary>One hour.</summary>
    Hour,

    /// <summary>One day, from 00:00 UTC.</summary>
    Day,

    /// <summary>One week, starting Monday 00:00 UTC.</summary>
    Week,

    /// <summary>One calendar month.</summary>
    Month,

    /// <summary>One calendar quarter (January, April, July, October).</summary>
    Quarter,

    /// <summary>One calendar year.</summary>
    Year,
}

/// <summary>
/// Time buckets over a date field, in UTC: either a calendar unit or a fixed length (a whole number of milliseconds, counted from the
/// Unix epoch plus <see cref="Offset"/>). Exactly one of <see cref="CalendarInterval"/> and <see cref="FixedInterval"/> must be set.
/// Bucket keys are the bucket start in milliseconds since the epoch. Values outside years 1 to 9999 are ignored for month, quarter
/// and year buckets.
/// </summary>
public sealed class DateHistogramAggregation : BucketAggregation
{
    /// <summary>Creates the request; set one of the interval properties.</summary>
    /// <param name="name">The result name.</param>
    /// <param name="field">A date field.</param>
    public DateHistogramAggregation(string name, string field)
        : base(name, field)
    {
    }

    /// <summary>A calendar unit.</summary>
    public DateInterval? CalendarInterval { get; init; }

    /// <summary>A fixed length, at least one millisecond.</summary>
    public TimeSpan? FixedInterval { get; init; }

    /// <summary>Shifts the boundaries of a fixed interval. Default zero; must stay zero for a calendar interval.</summary>
    public TimeSpan Offset { get; init; }

    /// <summary>As <see cref="HistogramAggregation.MinDocCount"/>: default 0 returns the empty buckets between the first and last used one.</summary>
    public long MinDocCount { get; init; }
}

/// <summary>Count, minimum, maximum, sum and average over the <em>values</em> of an integral, fractional or date field (a document with three values adds three). NaN values are skipped.</summary>
public sealed class StatsAggregation : Aggregation
{
    /// <summary>Creates the request.</summary>
    /// <param name="name">The result name.</param>
    /// <param name="field">The field.</param>
    public StatsAggregation(string name, string field)
        : base(name, field)
    {
    }
}

/// <summary>
/// The number of distinct values of a keyword, bool, integral, fractional or date field, estimated with HyperLogLog++ (Heule, Nunkesser
/// and Hall 2013): exact up to a few thousand distinct values, then within about 1.04 / sqrt(2^Precision) standard error.
/// </summary>
public sealed class CardinalityAggregation : Aggregation
{
    /// <summary>Creates the request.</summary>
    /// <param name="name">The result name.</param>
    /// <param name="field">The field.</param>
    public CardinalityAggregation(string name, string field)
        : base(name, field)
    {
    }

    /// <summary>The number of index bits, 4 to 18; the sketch holds 2^Precision registers. Default 14 (standard error 0.81%).</summary>
    public int Precision { get; init; } = 14;
}

/// <summary>
/// Percentiles of the values of an integral, fractional or date field, estimated with a t-digest (Dunning and Ertl 2019): accurate in
/// rank, tightest at the extremes. The minimum and maximum are exact.
/// </summary>
public sealed class PercentilesAggregation : Aggregation
{
    /// <summary>Creates the request.</summary>
    /// <param name="name">The result name.</param>
    /// <param name="field">The field.</param>
    public PercentilesAggregation(string name, string field)
        : base(name, field)
    {
    }

    /// <summary>The percentiles to report, each from 0 to 100. Default 1, 5, 25, 50, 75, 95, 99.</summary>
    public IReadOnlyList<double> Percents { get; init; } = [1, 5, 25, 50, 75, 95, 99];

    /// <summary>The digest's compression, 20 to 10,000: roughly the most centroids it keeps. Default 200.</summary>
    public double Compression { get; init; } = 200;
}
