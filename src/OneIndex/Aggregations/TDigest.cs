namespace OneIndex.Aggregations;

/// <summary>
/// A merging t-digest (Dunning and Ertl, "Computing Extremely Accurate Quantiles Using t-Digests", 2019) with the arcsine scale
/// function k1(q) = compression / (2 pi) * asin(2q - 1): a centroid may span at most one unit of k, so clusters are small near the
/// extremes and large in the middle. Values are buffered; a full buffer is sorted and merged with the centroids in one pass. The
/// result depends only on the order of values added, so it is deterministic. Minimum and maximum are tracked exactly.
/// </summary>
internal sealed class TDigest
{
    private readonly double _compression;
    private double[] _means;
    private double[] _weights;
    private int _centroids;
    private readonly double[] _bufferValues;
    private readonly double[] _bufferWeights;
    private int _buffered;
    private double[] _scratchMeans;
    private double[] _scratchWeights;
    private double _total;
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;

    public TDigest(double compression)
    {
        if (!(compression >= 20 && compression <= 10_000))
        {
            throw new ArgumentOutOfRangeException(nameof(compression), "Compression must be from 20 to 10,000.");
        }

        _compression = compression;
        int capacity = (int)Math.Ceiling(compression) + 16;
        _means = new double[capacity];
        _weights = new double[capacity];
        int buffer = Math.Max(64, 4 * (int)Math.Ceiling(compression));
        _bufferValues = new double[buffer];
        _bufferWeights = new double[buffer];
        _scratchMeans = new double[capacity + buffer];
        _scratchWeights = new double[capacity + buffer];
    }

    public double Count => _total + SumBuffer();

    public double Min => _min;

    public double Max => _max;

    public void Add(double value) => Add(value, 1.0);

    public void Add(double value, double weight)
    {
        if (double.IsNaN(value) || !(weight > 0))
        {
            return;
        }

        if (value < _min)
        {
            _min = value;
        }

        if (value > _max)
        {
            _max = value;
        }

        if (_buffered == _bufferValues.Length)
        {
            Compress();
        }

        _bufferValues[_buffered] = value;
        _bufferWeights[_buffered] = weight;
        _buffered++;
    }

    public void Merge(TDigest other)
    {
        other.Compress();
        for (int i = 0; i < other._centroids; i++)
        {
            Add(other._means[i], other._weights[i]);
        }

        // Centroids carry means, not extremes; the exact extremes are carried over.
        if (other._min < _min)
        {
            _min = other._min;
        }

        if (other._max > _max)
        {
            _max = other._max;
        }
    }

    /// <summary>The estimated value below which the fraction <paramref name="q"/> of the weight lies.</summary>
    /// <param name="q">From 0 to 1.</param>
    /// <returns>The estimate; NaN for an empty digest.</returns>
    public double Quantile(double q)
    {
        Compress();
        if (_centroids == 0)
        {
            return double.NaN;
        }

        if (q <= 0)
        {
            return _min;
        }

        if (q >= 1)
        {
            return _max;
        }

        // The digest is a piecewise-linear curve through (0, min), (centre of each centroid, its mean), (total, max), where a
        // centroid's centre is half its weight past the weight before it.
        double rank = q * _total;
        double previousRank = 0;
        double previousValue = _min;
        double before = 0;
        for (int i = 0; i < _centroids; i++)
        {
            double centre = before + (_weights[i] / 2);
            if (rank <= centre)
            {
                return Interpolate(previousRank, previousValue, centre, _means[i], rank);
            }

            previousRank = centre;
            previousValue = _means[i];
            before += _weights[i];
        }

        return Interpolate(previousRank, previousValue, _total, _max, rank);
    }

    private static double Interpolate(double x0, double y0, double x1, double y1, double x)
        => x1 <= x0 ? y1 : y0 + ((y1 - y0) * ((x - x0) / (x1 - x0)));

    private double SumBuffer()
    {
        double s = 0;
        for (int i = 0; i < _buffered; i++)
        {
            s += _bufferWeights[i];
        }

        return s;
    }

    private double K(double q) => _compression / (2 * Math.PI) * Math.Asin((2 * Math.Clamp(q, 0, 1)) - 1);

    private void Compress()
    {
        if (_buffered == 0)
        {
            return;
        }

        // Everything below reuses arrays, so a steady stream of values allocates nothing once the arrays have grown to fit.
        int n = _centroids + _buffered;
        if (_scratchMeans.Length < n)
        {
            _scratchMeans = new double[n * 2];
            _scratchWeights = new double[n * 2];
        }

        Array.Copy(_means, _scratchMeans, _centroids);
        Array.Copy(_weights, _scratchWeights, _centroids);
        Array.Copy(_bufferValues, 0, _scratchMeans, _centroids, _buffered);
        Array.Copy(_bufferWeights, 0, _scratchWeights, _centroids, _buffered);
        double[] means = _scratchMeans;
        double[] weights = _scratchWeights;
        Array.Sort(means, weights, 0, n); // a pure function of the input, so equal inputs give equal digests

        double total = 0;
        for (int i = 0; i < n; i++)
        {
            total += weights[i];
        }

        _total = total;
        _buffered = 0;
        int count = 0;
        double curMean = means[0];
        double curWeight = weights[0];
        double weightBefore = 0;
        double kStart = K(0);
        for (int i = 1; i < n; i++)
        {
            double proposed = curWeight + weights[i];
            if (K((weightBefore + proposed) / total) - kStart <= 1.0)
            {
                curMean += (means[i] - curMean) * (weights[i] / proposed);
                curWeight = proposed;
            }
            else
            {
                Emit(ref count, curMean, curWeight);
                weightBefore += curWeight;
                kStart = K(weightBefore / total);
                curMean = means[i];
                curWeight = weights[i];
            }
        }

        Emit(ref count, curMean, curWeight);
        _centroids = count;
    }

    private void Emit(ref int count, double mean, double weight)
    {
        if (count == _means.Length)
        {
            Array.Resize(ref _means, count * 2);
            Array.Resize(ref _weights, count * 2);
        }

        _means[count] = mean;
        _weights[count] = weight;
        count++;
    }
}
