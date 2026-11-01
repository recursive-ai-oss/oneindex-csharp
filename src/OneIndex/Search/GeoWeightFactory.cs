using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>
/// Builds the weights of the geo queries. The point index gives the documents whose points may lie in the query's rectangle (for a radius,
/// the rectangle that bounds the circle); each candidate is then confirmed against the exact coordinates in the column, so the answer
/// does not depend on the index's quantization. Matches go into a bit set per segment and score as the boost.
/// </summary>
internal static class GeoWeightFactory
{
    public static Weight Radius(Schema schema, GeoRadiusQuery q)
    {
        Require(schema, q.Field);
        if (!double.IsFinite(q.Latitude) || q.Latitude < -90 || q.Latitude > 90 || !double.IsFinite(q.Longitude) || q.Longitude < -180 || q.Longitude > 180)
        {
            throw new QueryException($"The centre ({q.Latitude}, {q.Longitude}) of a geo radius query on '{q.Field}' is not a valid latitude and longitude.");
        }

        if (!double.IsFinite(q.RadiusMeters) || q.RadiusMeters < 0)
        {
            throw new QueryException($"The radius {q.RadiusMeters} of a geo radius query on '{q.Field}' must be finite and not negative.");
        }

        GeoMath.BoundingBox(q.Latitude, q.Longitude, q.RadiusMeters, out double south, out double north, out double west, out double east);
        double lat = q.Latitude;
        double lng = q.Longitude;
        double radius = q.RadiusMeters;
        return new ConstantWeight($"geo radius on {q.Field}", q.Boost, leaf => Matches(leaf, q.Field, south, north, west, east, (la, lo) => GeoMath.HaversineMeters(lat, lng, la, lo) <= radius));
    }

    public static Weight BoundingBox(Schema schema, GeoBoundingBoxQuery q)
    {
        Require(schema, q.Field);
        if (!double.IsFinite(q.North) || !double.IsFinite(q.South) || q.North > 90 || q.South < -90 || q.South > q.North)
        {
            throw new QueryException($"The latitudes {q.South} to {q.North} of a geo bounding box query on '{q.Field}' must be within -90 to 90, south not above north.");
        }

        if (!double.IsFinite(q.West) || !double.IsFinite(q.East) || q.West < -180 || q.West > 180 || q.East < -180 || q.East > 180)
        {
            throw new QueryException($"The longitudes {q.West} and {q.East} of a geo bounding box query on '{q.Field}' must be within -180 to 180.");
        }

        double s = q.South;
        double n = q.North;
        double w = q.West;
        double e = q.East;
        return new ConstantWeight($"geo bounding box on {q.Field}", q.Boost, leaf => Matches(leaf, q.Field, s, n, w, e, (la, lo) => la >= s && la <= n && GeoMath.LongitudeInSpan(lo, w, e)));
    }

    private static void Require(Schema schema, string field)
    {
        if (!schema.TryGetField(field, out FieldDefinition? def))
        {
            throw new QueryException($"The schema has no field '{field}'.");
        }

        if (def.Type != FieldType.GeoPoint)
        {
            throw new QueryException($"A geo query needs a geo point field; '{field}' is {def.Type}.");
        }

        if (!def.Indexed || !def.Column)
        {
            throw new QueryException($"Geo field '{field}' must be indexed and keep a column to be searched.");
        }
    }

    private static BitSetIterator? Matches(LeafContext leaf, string field, double south, double north, double west, double east, Func<double, double, bool> inside)
    {
        if (leaf.Reader.GetPoints(field) is not IGeoPointIndexReader points || leaf.Reader.GetColumn(field) is not { } column)
        {
            return null;
        }

        var words = new ulong[(leaf.Reader.MaxDoc + 63) >> 6];
        bool any = false;
        DocIdSetIterator candidates = points.GetCandidates(south, north, west, east);
        for (int d = candidates.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = candidates.NextDoc())
        {
            int values = column.ValueCount(d);
            for (int i = 0; i < values; i++)
            {
                (double la, double lo) = column.GetGeoPoint(d, i);
                if (inside(la, lo))
                {
                    words[d >> 6] |= 1UL << (d & 63);
                    any = true;
                    break;
                }
            }
        }

        return any ? new BitSetIterator(words) : null;
    }
}
