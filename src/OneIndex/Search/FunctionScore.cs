using System.Globalization;
using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>How the values of the functions that apply to a document are combined into one function score. Names and meanings follow Elasticsearch's <c>score_mode</c>.</summary>
public enum FunctionScoreMode : byte
{
    /// <summary>The product of the function values (the default).</summary>
    Multiply,

    /// <summary>The sum.</summary>
    Sum,

    /// <summary>The weighted average: the sum of the values (which already include their weights) divided by the sum of the weights that applied. 0 when those weights are all 0.</summary>
    Average,

    /// <summary>The value of the first function, in the order given, that applies to the document.</summary>
    First,

    /// <summary>The largest value.</summary>
    Max,

    /// <summary>The smallest value.</summary>
    Min,
}

/// <summary>How the function score and the score of the wrapped query make the document's score. Names and meanings follow Elasticsearch's <c>boost_mode</c>.</summary>
public enum FunctionBoostMode : byte
{
    /// <summary>Query score times function score (the default).</summary>
    Multiply,

    /// <summary>The function score alone.</summary>
    Replace,

    /// <summary>Query score plus function score.</summary>
    Sum,

    /// <summary>The mean of the two.</summary>
    Average,

    /// <summary>The larger of the two.</summary>
    Max,

    /// <summary>The smaller of the two.</summary>
    Min,
}

/// <summary>What a <see cref="FieldValueFactorFunction"/> does to <c>factor × value</c>. Logarithms of values that are not positive have no value (see <see cref="FieldValueFactorFunction"/>).</summary>
public enum FieldValueModifier : byte
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>The common (base 10) logarithm.</summary>
    Log,

    /// <summary>The common logarithm of 1 plus the value.</summary>
    Log1p,

    /// <summary>The common logarithm of 2 plus the value.</summary>
    Log2p,

    /// <summary>The natural logarithm.</summary>
    Ln,

    /// <summary>The natural logarithm of 1 plus the value.</summary>
    Ln1p,

    /// <summary>The natural logarithm of 2 plus the value.</summary>
    Ln2p,

    /// <summary>The square.</summary>
    Square,

    /// <summary>The square root.</summary>
    Sqrt,

    /// <summary>One over the value.</summary>
    Reciprocal,
}

/// <summary>The shape of a decay function (Elasticsearch's published formulas, docs/function-scores.md).</summary>
public enum DecayShape : byte
{
    /// <summary>Normal decay: <c>exp(-d² / 2σ²)</c>.</summary>
    Gauss,

    /// <summary>Exponential decay: <c>exp(λ·d)</c> with a negative λ.</summary>
    Exp,

    /// <summary>Linear decay, reaching 0 at <c>scale / (1 - decay)</c> beyond the offset.</summary>
    Linear,
}

/// <summary>
/// One scoring function of a <see cref="FunctionScoreQuery"/>: a number computed from a document's column values, multiplied by
/// <see cref="Weight"/>. A function that has no value for a document (no <see cref="Filter"/> match, a missing value without a
/// default, or a result that is negative or not finite) does not take part for that document.
/// </summary>
public abstract record ScoreFunction
{
    private protected ScoreFunction()
    {
    }

    /// <summary>Multiplies the function's value. Finite and not negative; default 1.</summary>
    public float Weight { get; init; } = 1f;

    /// <summary>Restricts the function to documents that match this query; its own score does not matter. <see langword="null"/> (default) means every document.</summary>
    public Query? Filter { get; init; }

    internal abstract void Validate(Schema schema);

    internal abstract LeafFunction BindLeaf(LeafContext leaf);

    internal abstract string Describe();

    internal void ValidateCommon()
    {
        if (!float.IsFinite(Weight) || Weight < 0f)
        {
            throw new QueryException($"The weight {Weight} of a score function must be finite and not negative.");
        }
    }

    internal static FieldDefinition RequireColumn(Schema schema, string field, string what, Func<FieldType, bool> accepts, string accepted)
    {
        if (!schema.TryGetField(field, out FieldDefinition? def))
        {
            throw new QueryException($"The schema has no field '{field}'.");
        }

        if (!accepts(def.Type))
        {
            throw new QueryException($"{what} needs a {accepted} field; '{field}' is {def.Type}.");
        }

        if (!def.Column)
        {
            throw new QueryException($"{what} reads a column, and '{field}' keeps none; mark it filterable or sortable.");
        }

        return def;
    }
}

/// <summary>A function that is only its weight: every document it applies to gets the value 1, times <see cref="ScoreFunction.Weight"/>. With a filter it boosts the documents that match it.</summary>
public sealed record WeightFunction : ScoreFunction
{
    internal override void Validate(Schema schema) => ValidateCommon();

    internal override LeafFunction BindLeaf(LeafContext leaf) => new ConstantLeaf();

    internal override string Describe() => "weight";

    private sealed class ConstantLeaf : LeafFunction
    {
        public override bool TryGet(int docId, out double raw)
        {
            raw = 1d;
            return true;
        }

        public override string Explain(int docId, double raw) => "constant 1";
    }
}

/// <summary>
/// The value of a numeric, date or bool column after a modifier: <c>modifier(Factor × value)</c> (Elasticsearch's <c>field_value_factor</c>).
/// Dates count milliseconds since 1970 and bools 0 or 1. Of several values the first is used. A document with no value uses
/// <see cref="Missing"/> (factor and modifier still apply) or, when that is <see langword="null"/>, does not take part. A result that is
/// negative or not finite (a logarithm of 0 or less, a square root of a negative number, a reciprocal of 0) also means no value.
/// </summary>
/// <param name="Field">The field; it must keep a column.</param>
public sealed record FieldValueFactorFunction(string Field) : ScoreFunction
{
    /// <summary>Multiplies the value before the modifier. Finite; default 1.</summary>
    public double Factor { get; init; } = 1d;

    /// <summary>Applied to <c>Factor × value</c>. Default <see cref="FieldValueModifier.None"/>.</summary>
    public FieldValueModifier Modifier { get; init; }

    /// <summary>The value used for a document that has none. <see langword="null"/> (default): the function does not apply to it.</summary>
    public double? Missing { get; init; }

    internal override void Validate(Schema schema)
    {
        ValidateCommon();
        _ = RequireColumn(schema, Field, "A field value factor", static t => t is FieldType.Integral or FieldType.Fractional or FieldType.Date or FieldType.Bool, "numeric, date or bool");
        if (!double.IsFinite(Factor) || (Missing is { } m && !double.IsFinite(m)))
        {
            throw new QueryException($"The factor and the missing value of a field value factor on '{Field}' must be finite.");
        }
    }

    internal override LeafFunction BindLeaf(LeafContext leaf) => new Leaf(this, leaf.Reader.GetColumn(Field));

    internal override string Describe()
        => $"field value factor on {Field}: {(Modifier == FieldValueModifier.None ? string.Empty : FunctionNames.Modifier(Modifier) + "(")}factor {Num.Format(Factor)} × value{(Modifier == FieldValueModifier.None ? string.Empty : ")")}";

    internal static double Apply(FieldValueModifier modifier, double x) => modifier switch
    {
        FieldValueModifier.None => x,
        FieldValueModifier.Log => Math.Log10(x),
        FieldValueModifier.Log1p => Math.Log10(1d + x),
        FieldValueModifier.Log2p => Math.Log10(2d + x),
        FieldValueModifier.Ln => Math.Log(x),
        FieldValueModifier.Ln1p => Math.Log(1d + x),
        FieldValueModifier.Ln2p => Math.Log(2d + x),
        FieldValueModifier.Square => x * x,
        FieldValueModifier.Sqrt => Math.Sqrt(x),
        _ => 1d / x,
    };

    private sealed class Leaf(FieldValueFactorFunction f, IColumnReader? column) : LeafFunction
    {
        public override bool TryGet(int docId, out double raw)
        {
            double v;
            if (column is not null && column.ValueCount(docId) > 0)
            {
                v = Num.Read(column, docId, 0);
            }
            else if (f.Missing is { } m)
            {
                v = m;
            }
            else
            {
                raw = 0d;
                return false;
            }

            raw = Apply(f.Modifier, f.Factor * v);
            return double.IsFinite(raw) && raw >= 0d;
        }

        public override string Explain(int docId, double raw)
        {
            string used = column is not null && column.ValueCount(docId) > 0 ? Num.Format(Num.Read(column, docId, 0)) : "missing " + Num.Format(f.Missing ?? 0d);
            return $"{FunctionNames.Modifier(f.Modifier)}(factor {Num.Format(f.Factor)} × value {used})";
        }
    }
}

/// <summary>
/// A decay of a numeric or date column with the distance from an origin (Elasticsearch's <c>gauss</c>, <c>exp</c> and <c>linear</c>): 1 within
/// <see cref="Offset"/> of the origin, <see cref="Decay"/> at <see cref="Scale"/> beyond that. Dates count milliseconds since 1970. Of several values the nearest to the origin counts. A document with no value scores 1.
/// </summary>
/// <param name="Shape">The curve.</param>
/// <param name="Field">The field; it must keep a column.</param>
/// <param name="Origin">The centre, in the field's units. Finite.</param>
/// <param name="Scale">The distance beyond the offset at which the value is <see cref="Decay"/>. Finite and above 0.</param>
public sealed record DecayFunction(DecayShape Shape, string Field, double Origin, double Scale) : ScoreFunction
{
    /// <summary>The distance from the origin within which nothing decays. Finite and not negative; default 0.</summary>
    public double Offset { get; init; }

    /// <summary>The value at <see cref="Scale"/> beyond the offset: above 0 and below 1; default 0.5.</summary>
    public double Decay { get; init; } = 0.5d;

    internal override void Validate(Schema schema)
    {
        ValidateCommon();
        _ = RequireColumn(schema, Field, "A decay function", static t => t is FieldType.Integral or FieldType.Fractional or FieldType.Date, "numeric or date");
        DecayMath.Validate(Field, Origin, Scale, Offset, Decay);
    }

    internal override LeafFunction BindLeaf(LeafContext leaf) => new Leaf(this, leaf.Reader.GetColumn(Field));

    internal override string Describe()
        => $"{DecayMath.Name(Shape)} decay on {Field}: origin {Num.Format(Origin)}, scale {Num.Format(Scale)}, offset {Num.Format(Offset)}, decay {Num.Format(Decay)}";

    private sealed class Leaf(DecayFunction f, IColumnReader? column) : LeafFunction
    {
        public override bool TryGet(int docId, out double raw)
        {
            raw = 1d;
            int n = column?.ValueCount(docId) ?? 0;
            if (n == 0)
            {
                return true;
            }

            double distance = double.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                distance = Math.Min(distance, Math.Abs(Num.Read(column!, docId, i) - f.Origin));
            }

            raw = DecayMath.Compute(f.Shape, distance, f.Offset, f.Scale, f.Decay);
            return true;
        }

        public override string Explain(int docId, double raw)
        {
            int n = column?.ValueCount(docId) ?? 0;
            return n == 0 ? "no value, so 1" : $"{DecayMath.Name(f.Shape)} of the distance to the nearest of {n} value(s)";
        }
    }
}

/// <summary>
/// A decay of a geo point column with the great-circle distance in metres from a point (<see cref="GeoMath.HaversineMeters"/>), with the
/// same curves as <see cref="DecayFunction"/>. Of several points the nearest counts. A document with no point scores 1.
/// </summary>
/// <param name="Shape">The curve.</param>
/// <param name="Field">The geo point field; it must keep a column.</param>
/// <param name="Latitude">The origin's latitude in degrees, -90 to 90.</param>
/// <param name="Longitude">The origin's longitude in degrees, -180 to 180.</param>
/// <param name="Scale">The distance in metres beyond the offset at which the value is <see cref="Decay"/>. Finite and above 0.</param>
public sealed record GeoDecayFunction(DecayShape Shape, string Field, double Latitude, double Longitude, double Scale) : ScoreFunction
{
    /// <summary>The distance in metres within which nothing decays. Finite and not negative; default 0.</summary>
    public double Offset { get; init; }

    /// <summary>The value at <see cref="Scale"/> beyond the offset: above 0 and below 1; default 0.5.</summary>
    public double Decay { get; init; } = 0.5d;

    internal override void Validate(Schema schema)
    {
        ValidateCommon();
        _ = RequireColumn(schema, Field, "A geo decay function", static t => t == FieldType.GeoPoint, "geo point");
        if (!double.IsFinite(Latitude) || Latitude is < -90 or > 90 || !double.IsFinite(Longitude) || Longitude is < -180 or > 180)
        {
            throw new QueryException($"The origin ({Latitude}, {Longitude}) of a geo decay on '{Field}' is not a valid latitude and longitude.");
        }

        DecayMath.Validate(Field, 0d, Scale, Offset, Decay);
    }

    internal override LeafFunction BindLeaf(LeafContext leaf) => new Leaf(this, leaf.Reader.GetColumn(Field));

    internal override string Describe()
        => $"{DecayMath.Name(Shape)} decay on distance from {Field} to ({Num.Format(Latitude)}, {Num.Format(Longitude)}): scale {Num.Format(Scale)} m, offset {Num.Format(Offset)} m, decay {Num.Format(Decay)}";

    private sealed class Leaf(GeoDecayFunction f, IColumnReader? column) : LeafFunction
    {
        public override bool TryGet(int docId, out double raw)
        {
            raw = 1d;
            int n = column?.ValueCount(docId) ?? 0;
            if (n == 0)
            {
                return true;
            }

            raw = DecayMath.Compute(f.Shape, Nearest(docId, n), f.Offset, f.Scale, f.Decay);
            return true;
        }

        public override string Explain(int docId, double raw)
        {
            int n = column?.ValueCount(docId) ?? 0;
            return n == 0 ? "no point, so 1" : $"{DecayMath.Name(f.Shape)} of the nearest distance {Num.Format(Nearest(docId, n))} m";
        }

        private double Nearest(int docId, int n)
        {
            double best = double.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                (double la, double lo) = column!.GetGeoPoint(docId, i);
                best = Math.Min(best, GeoMath.HaversineMeters(f.Latitude, f.Longitude, la, lo));
            }

            return best;
        }
    }
}

/// <summary>
/// A function that runs your code over the column readers of a segment. It is the low-level escape hatch for scoring that the other
/// functions cannot express, and it is not available in query JSON. <paramref name="BindSegment"/> runs once per segment and returns what runs per document;
/// that returns the function's value, or <see langword="null"/> when the function does not apply. A value that is negative or not finite also means it does not apply.
/// </summary>
/// <param name="Description">What the function is, shown by explain.</param>
/// <param name="BindSegment">Called with a segment's reader; fetch columns here (<see cref="ISegmentReader.GetColumn"/>) rather than per document.</param>
public sealed record CustomFunction(string Description, Func<ISegmentReader, Func<int, double?>> BindSegment) : ScoreFunction
{
    internal override void Validate(Schema schema)
    {
        ValidateCommon();
        if (string.IsNullOrWhiteSpace(Description) || BindSegment is null)
        {
            throw new QueryException("A custom score function needs a description and a bind delegate.");
        }
    }

    internal override LeafFunction BindLeaf(LeafContext leaf) => new Leaf(Description, BindSegment(leaf.Reader));

    internal override string Describe() => "custom function " + Description;

    private sealed class Leaf(string description, Func<int, double?> compute) : LeafFunction
    {
        public override bool TryGet(int docId, out double raw)
        {
            double? v = compute(docId);
            raw = v ?? 0d;
            return v is { } x && double.IsFinite(x) && x >= 0d;
        }

        public override string Explain(int docId, double raw) => description;
    }
}

/// <summary>
/// Changes the score of another query with functions of column values (Elasticsearch's <c>function_score</c>; formulas and edge cases in docs/function-scores.md).
/// The documents that match are those of <see cref="Inner"/> (and not fewer, unless <see cref="MinScore"/> is set). Each function that applies to
/// a document gives a value (its formula times its weight); the values combine by <see cref="ScoreMode"/>, are capped at <see cref="MaxBoost"/>, combine with the
/// inner score by <see cref="BoostMode"/> and are multiplied by <see cref="Query.Boost"/>. A document no function applies to keeps the inner score.
/// Scores stay finite: a product that overflows becomes the largest float.
/// </summary>
/// <param name="Inner">The query that selects and scores documents; <see cref="MatchAllQuery"/> scores them 1.</param>
/// <param name="Functions">The functions, 1 to 32, in order (order matters for <see cref="FunctionScoreMode.First"/>).</param>
public sealed record FunctionScoreQuery(Query Inner, IReadOnlyList<ScoreFunction> Functions) : Query
{
    /// <summary>The most functions in one query.</summary>
    public const int MaxFunctions = 32;

    /// <summary>How the function values combine. Default <see cref="FunctionScoreMode.Multiply"/>.</summary>
    public FunctionScoreMode ScoreMode { get; init; }

    /// <summary>How the function score and the inner score combine. Default <see cref="FunctionBoostMode.Multiply"/>.</summary>
    public FunctionBoostMode BoostMode { get; init; }

    /// <summary>The ceiling of the combined function score, before it meets the inner score. Above 0; default <see cref="float.MaxValue"/>.</summary>
    public float MaxBoost { get; init; } = float.MaxValue;

    /// <summary>Documents that score below this are dropped (so every match is scored). <see langword="null"/> (default) keeps all.</summary>
    public float? MinScore { get; init; }

    /// <summary>Compares the functions one by one (the generated record equality would compare the list by reference).</summary>
    /// <param name="other">The other query.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public bool Equals(FunctionScoreQuery? other)
        => other is not null && Boost.Equals(other.Boost) && Inner.Equals(other.Inner) && ScoreMode == other.ScoreMode && BoostMode == other.BoostMode
            && MaxBoost.Equals(other.MaxBoost) && MinScore.Equals(other.MinScore) && Functions.SequenceEqual(other.Functions);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // In-process hash tables only; never persisted.
        var hash = default(HashCode);
        hash.Add(Inner);
        hash.Add(Boost);
        hash.Add(ScoreMode);
        hash.Add(BoostMode);
        foreach (ScoreFunction f in Functions)
        {
            hash.Add(f);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitFunctionScore(this);
}

internal abstract class LeafFunction
{
    public abstract bool TryGet(int docId, out double raw);

    public abstract string Explain(int docId, double raw);
}

internal static class FunctionNames
{
    public static readonly string[] Modifiers = ["none", "log", "log1p", "log2p", "ln", "ln1p", "ln2p", "square", "sqrt", "reciprocal"];
    public static readonly string[] Shapes = ["gauss", "exp", "linear"];
    public static readonly string[] ScoreModes = ["multiply", "sum", "avg", "first", "max", "min"];
    public static readonly string[] BoostModes = ["multiply", "replace", "sum", "avg", "max", "min"];

    public static string Modifier(FieldValueModifier m) => Modifiers[(int)m];
}

internal static class Num
{
    public static string Format(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    public static double Read(IColumnReader column, int docId, int index)
        => column.Type == FieldType.Fractional ? column.GetFloat64(docId, index) : column.GetInt64(docId, index);
}

internal static class DecayMath
{
    public static string Name(DecayShape s) => FunctionNames.Shapes[(int)s];

    public static void Validate(string field, double origin, double scale, double offset, double decay)
    {
        if (!double.IsFinite(origin))
        {
            throw new QueryException($"The origin of a decay on '{field}' must be finite.");
        }

        if (!double.IsFinite(scale) || scale <= 0d)
        {
            throw new QueryException($"The scale {scale} of a decay on '{field}' must be finite and above 0.");
        }

        if (!double.IsFinite(offset) || offset < 0d)
        {
            throw new QueryException($"The offset {offset} of a decay on '{field}' must be finite and not negative.");
        }

        if (!(decay > 0d && decay < 1d))
        {
            throw new QueryException($"The decay {decay} of a decay on '{field}' must be above 0 and below 1.");
        }
    }

    // Elasticsearch's published formulas (function_score documentation), with d = max(0, distance - offset):
    //   gauss  S = exp(-d^2 / (2 sigma^2)),  sigma^2 = -scale^2 / (2 ln(decay))
    //   exp    S = exp(lambda d),            lambda = ln(decay) / scale
    //   linear S = max((s - d) / s, 0),      s = scale / (1 - decay)
    public static double Compute(DecayShape shape, double distance, double offset, double scale, double decay)
    {
        double d = Math.Max(0d, distance - offset);
        switch (shape)
        {
            case DecayShape.Gauss:
                double sigma2 = -(scale * scale) / (2d * Math.Log(decay));
                return Math.Exp(-(d * d) / (2d * sigma2));
            case DecayShape.Exp:
                return Math.Exp(Math.Log(decay) / scale * d);
            default:
                double s = scale / (1d - decay);
                return Math.Max((s - d) / s, 0d);
        }
    }
}
